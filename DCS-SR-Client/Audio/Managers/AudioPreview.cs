using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Utility;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Singletons;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Opus.Core;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Settings;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NLog;
using OpenFreqAudio;
using Application = Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Opus.Application;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Audio.Utility;
using System.Windows.Forms;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Audio.Managers;

internal class AudioPreview
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly AudioInputSingleton _audioInputSingleton = AudioInputSingleton.Instance;
    private readonly AudioOutputSingleton _audioOutputSingleton = AudioOutputSingleton.Instance;
    private readonly ProfileSettingsStore _profileSettings = GlobalSettingsStore.Instance.ProfileSettingsStore;

    private readonly Queue<short> _micInputQueue = new(Constants.MIC_SEGMENT_FRAMES * 3);

    private readonly object lockob = new();
    private BufferedWaveProvider _buffBufferedWaveProvider;
    private OpusDecoder _decoder;
    private OpusEncoder _encoder;
    private EventDrivenResampler _resampler;

    private float _speakerBoost = 1.0f;

    private SpeexProcessor _speex;
    private VolumeSampleProviderWithPeak _volumeSampleProvider;

    private WasapiCapture _wasapiCapture;

    private WaveFileWriter _waveFile;
    private SRSWasapiOut _waveOut;

    private bool windowsN;
    private bool _useOpenFreqPath;
    private OpenFreqReceiverEffectsPipeline _openFreqPipeline;
    private ProceduralAmbientProcessor _proceduralAmbient;
    private ProceduralAmbientProcessor.Profile? _proceduralProfile;
    private CachedAudioEffect _wavAmbient;
    private int _wavAmbientProgress;
    private string _previewAircraft = "f16";

    public float MicBoost { get; set; } = 1.0f;

    public bool IsPreviewing => _waveOut != null;

    public float SpeakerBoost
    {
        get => _speakerBoost;
        set
        {
            _speakerBoost = value;
            if (_volumeSampleProvider != null) _volumeSampleProvider.Volume = value;
        }
    }

    public float MicMax { get; set; } = -100;
    public float SpeakerMax { get; set; } = -100;

    public string PreviewAircraft
    {
        get => _previewAircraft;
        set
        {
            _previewAircraft = string.IsNullOrWhiteSpace(value) ? "f16" : value.Trim().ToLowerInvariant();
            _proceduralAmbient = null;
            _proceduralProfile = null;
            _wavAmbient = null;
            _wavAmbientProgress = 0;
        }
    }

    public void StartPreview(bool windowsN)
    {
        this.windowsN = windowsN;
        PreviewAircraft = _profileSettings.GetClientSettingString(ProfileSettingsKeys.PreviewAircraft);
        _useOpenFreqPath = OpenFreqReceiverEffectsPipeline.IsOpenFreqAmEnabled();
        _openFreqPipeline = _useOpenFreqPath
            ? new OpenFreqReceiverEffectsPipeline(radioId: 0, sampleRate: Constants.OUTPUT_SAMPLE_RATE)
            : null;

        try
        {
            MMDevice speakers = null;
            if (_audioOutputSingleton.SelectedAudioOutput.Value == null)
                speakers = SRSWasapiOut.GetDefaultAudioEndpoint();
            else
                speakers = (MMDevice)_audioOutputSingleton.SelectedAudioOutput.Value;

            _waveOut = new SRSWasapiOut(speakers, AudioClientShareMode.Shared, true, 80, windowsN);

            _buffBufferedWaveProvider =
                new BufferedWaveProvider(new WaveFormat(Constants.OUTPUT_SAMPLE_RATE, 16, 1));
            _buffBufferedWaveProvider.ReadFully = true;
            _buffBufferedWaveProvider.DiscardOnBufferOverflow = true;

            ISampleProvider sampleProvider = _buffBufferedWaveProvider.ToSampleProvider();
            if (!_useOpenFreqPath)
                sampleProvider = new RadioFilter(sampleProvider);

            //add final volume boost to all mixed audio
            _volumeSampleProvider = new VolumeSampleProviderWithPeak(sampleProvider,
                peak => { SpeakerMax = (float)VolumeConversionHelper.ConvertFloatToDB(peak); });
            _volumeSampleProvider.Volume = SpeakerBoost;

            if (speakers.AudioClient.MixFormat.Channels == 1)
            {
                if (_volumeSampleProvider.WaveFormat.Channels == 2)
                    _waveOut.Init(_volumeSampleProvider.ToMono());
                else
                    _waveOut.Init(_volumeSampleProvider);
            }
            else
            {
                if (_volumeSampleProvider.WaveFormat.Channels == 1)
                    _waveOut.Init(_volumeSampleProvider.ToStereo());
                else
                    _waveOut.Init(_volumeSampleProvider);
            }

            _waveOut.Play();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error starting audio Output - Quitting! " + ex.Message);

            ShowOutputError("Problem Initialising Audio Output!");

            Environment.Exit(1);
        }

        try
        {
            _speex = new SpeexProcessor(Constants.MIC_SEGMENT_FRAMES, Constants.MIC_SAMPLE_RATE);
            _encoder = OpusEncoder.Create(Constants.MIC_SAMPLE_RATE, 1, Application.Voip);
            _encoder.ForwardErrorCorrection = false;
            _decoder = OpusDecoder.Create(Constants.OUTPUT_SAMPLE_RATE, 1);
            _decoder.ForwardErrorCorrection = false;
            _decoder.MaxDataBytes = Constants.OUTPUT_SAMPLE_RATE * 4;

            var device = (MMDevice)_audioInputSingleton.SelectedAudioInput.Value;

            if (device == null) device = WasapiCapture.GetDefaultCaptureDevice();

            if (!WineDetector.IsRunningUnderWine())
            {
                try
                {
                    device.AudioEndpointVolume.Mute = false;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to forcibly unmute: " + ex.Message);
                }
            }

            _wasapiCapture = new WasapiCapture(device, true);
            _wasapiCapture.ShareMode = AudioClientShareMode.Shared;
            _wasapiCapture.DataAvailable += WasapiCaptureOnDataAvailable;
            _wasapiCapture.RecordingStopped += WasapiCaptureOnRecordingStopped;

            _wasapiCapture.StartRecording();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error starting audio Input - Quitting! " + ex.Message);
            ShowInputError();

            Environment.Exit(1);
        }
    }

    private void ShowInputError()
    {
        var audioInputErrorDialog = TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = "Audio Input Error",
            Heading = "Problem initialising Audio Input!",
            Text =
                "If you are using Windows 10/11, check privacy settings (allow apps to access your microphone).\nAlternatively, try a different Input device.\n\nSee clientlog.txt in the Client install folder for support.",
            Icon = TaskDialogIcon.Error,
            Buttons =
                {
                    new TaskDialogButton
                    {
                        Text = "OPEN PRIVACY SETTINGS",
                        Tag = 1
                    },
                    new TaskDialogButton
                    {
                        Text = "CLOSE",
                        Tag = 3
                    }
                }
        });

        if (audioInputErrorDialog.Tag is int choice && choice == 1)
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone")
            { UseShellExecute = true });
        }
    }

    private void ShowOutputError(string message)
    {
        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = "Audio Output Error",
            Heading = message,
            Text =
                "Try a different output device in 455th SRS settings, and check Windows privacy permissions if needed.\n\n" +
                "See clientlog.txt in the Client install folder for support.",
            Icon = TaskDialogIcon.Error,
            Buttons =
            {
                new TaskDialogButton
                {
                    Text = "CLOSE",
                    Tag = 3
                }
            }
        });
    }

    private void WasapiCaptureOnRecordingStopped(object sender, StoppedEventArgs e)
    {
        Logger.Error("Recording Stopped");
    }

    private void WasapiCaptureOnDataAvailable(object sender, WaveInEventArgs e)
    {
        if (_resampler == null)
            _resampler = new EventDrivenResampler(windowsN, _wasapiCapture.WaveFormat,
                new WaveFormat(Constants.MIC_SAMPLE_RATE, 16, 1));

        if (e.BytesRecorded <= 0)
            return;

        var resampledPCM16Bit = _resampler.Resample(e.Buffer, e.BytesRecorded);

        short[] pcmShort = null;

        for (var i = 0; i < resampledPCM16Bit.Length; i++) _micInputQueue.Enqueue(resampledPCM16Bit[i]);

        while (pcmShort != null || _micInputQueue.Count >= Constants.MIC_SEGMENT_FRAMES)
        {
            if (pcmShort == null)
            {
                pcmShort = new short[Constants.MIC_SEGMENT_FRAMES];

                for (var i = 0; i < Constants.MIC_SEGMENT_FRAMES; i++)
                    pcmShort[i] = _micInputQueue.Dequeue();
            }

            try
            {
                _speex.Process(new ArraySegment<short>(pcmShort));

                MicMax = (float)VolumeConversionHelper.CalculateRMS(pcmShort);

                var pcmBytes = new byte[pcmShort.Length * 2];
                Buffer.BlockCopy(pcmShort, 0, pcmBytes, 0, pcmBytes.Length);

                int len;
                var buff = _encoder.Encode(pcmBytes, pcmBytes.Length, out len);

                if (buff != null && len > 0)
                {
                    var encoded = new byte[len];
                    Buffer.BlockCopy(buff, 0, encoded, 0, len);

                    var decodedLength = 0;
                    var decodedBytes = _decoder.Decode(encoded, len, out decodedLength);

                    if (_useOpenFreqPath && _openFreqPipeline != null)
                    {
                        var floats = Pcm16ToFloat(decodedBytes, decodedLength);
                        ApplyPreviewAmbient(floats);
                        var processed = new float[floats.Length];
                        var segment = new TransmissionSegment(
                            floats,
                            Modulation.AM,
                            frequencyHz: 251_000_000,
                            receivingPower: 0.05,
                            lineOfSightLoss: 0f,
                            clientGuid: "PREVIEW");
                        _openFreqPipeline.ProcessSegments(processed, new[] { segment });
                        var outBytes = FloatToPcm16(processed);
                        _buffBufferedWaveProvider.AddSamples(outBytes, 0, outBytes.Length);
                    }
                    else
                    {
                        _buffBufferedWaveProvider.AddSamples(decodedBytes, 0, decodedLength);
                    }
                }
                else
                {
                    Logger.Error(
                        $"Invalid Bytes for Encoding - {e.BytesRecorded} should be {Constants.MIC_SEGMENT_FRAMES} ");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error encoding Opus! " + ex.Message);
            }

            pcmShort = null;
        }
    }

    private void ApplyPreviewAmbient(Span<float> samples)
    {
        if (string.IsNullOrEmpty(PreviewAircraft) || samples.IsEmpty)
            return;

        if (ProceduralAmbientProcessor.TryMapAircraft(PreviewAircraft, out var profile))
        {
            if (_proceduralAmbient == null || _proceduralProfile != profile)
            {
                _proceduralAmbient = new ProceduralAmbientProcessor(Constants.OUTPUT_SAMPLE_RATE, profile);
                _proceduralProfile = profile;
            }

            _proceduralAmbient.Process(samples, 1f);
            return;
        }

        _wavAmbient ??= CachedAudioEffectProvider.Instance.GetAmbientEffect(PreviewAircraft);
        if (_wavAmbient == null || !_wavAmbient.Loaded || _wavAmbient.AudioEffectFloat == null)
            return;

        var effect = _wavAmbient.AudioEffectFloat;
        for (var i = 0; i < samples.Length; i++)
        {
            if (_wavAmbientProgress >= effect.Length)
                _wavAmbientProgress = 0;

            var mixed = samples[i] + effect[_wavAmbientProgress] * 0.35f;
            if (mixed is > -1f and < 1f)
                samples[i] = mixed;

            _wavAmbientProgress++;
        }
    }

    private static float[] Pcm16ToFloat(byte[] pcm, int length)
    {
        var samples = length / 2;
        var floats = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            var sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            floats[i] = sample / 32768f;
        }

        return floats;
    }

    private static byte[] FloatToPcm16(float[] floats)
    {
        var bytes = new byte[floats.Length * 2];
        for (var i = 0; i < floats.Length; i++)
        {
            var clamped = Math.Clamp(floats[i], -1f, 1f);
            var sample = (short)Math.Round(clamped * 32767f);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return bytes;
    }

    public void StopEncoding()
    {
        lock (lockob)
        {
            _wasapiCapture?.StopRecording();
            _wasapiCapture?.Dispose();
            _wasapiCapture = null;

            _resampler?.Dispose(true);
            _resampler = null;

            _waveOut?.Dispose();
            _waveOut = null;

            _encoder?.Dispose();
            _encoder = null;

            _decoder?.Dispose();
            _decoder = null;

            _speex?.Dispose();
            _speex = null;

            _waveFile?.Flush();
            _waveFile?.Dispose();
            _waveFile = null;

            _openFreqPipeline?.Reset();
            _openFreqPipeline = null;
            _proceduralAmbient = null;
            _proceduralProfile = null;
            _wavAmbient = null;

            SpeakerMax = -100;
            MicMax = -100;
        }
    }
}
