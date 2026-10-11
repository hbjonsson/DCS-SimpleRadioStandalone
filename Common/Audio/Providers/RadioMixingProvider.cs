using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Recording;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Utility;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Settings;
using NAudio.SoundFont;
using NAudio.Utils;
using NAudio.Wave;
using NLog;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;

public class RadioMixingProvider : ISampleProvider
{
    private static readonly Logger logger = LogManager.GetCurrentClassLogger();
    private readonly AudioRecordingManager _audioRecordingManager = AudioRecordingManager.Instance;

    private readonly CachedAudioEffectProvider _cachedAudioEffectsProvider;
    private readonly CircularFloatBuffer effectsBuffer;


    private readonly ClientEffectsPipeline pipeline = new();
    private readonly OpenFreqReceiverEffectsPipeline openFreqPipeline;

    private readonly ProfileSettingsStore profileSettings =
        GlobalSettingsStore.Instance.ProfileSettingsStore;

    private readonly int radioId;
    private readonly List<ClientAudioProvider> sources;

    private Modulation lastModulation = Modulation.DISABLED;
    private bool IsReceiving { get; set; } = false;

    private float[] mixBuffer;
    private float[] _openFreqScratch;
    private float[] _srsScratch;
    private int availableInBuffer = 0;
    private long _lastEngineRefresh;
    private ReceiverEffectsEngine _receiverEffectsEngine = ReceiverEffectsEngine.SRS;
    private int _openFreqDrainSamplesRemaining;
    /// <summary>Last OpenFreq AM radio volume; reused for post-TX drain so the tail follows the knob.</summary>
    private float _openFreqRadioVolume = 1f;
    /// <summary>Receiving-radio volume used to scale RX/TX tones for this radio.</summary>
    private float _radioToneVolume = 1f;

    //  private readonly WaveFileWriter waveWriter;
    public RadioMixingProvider(WaveFormat waveFormat, int radioId)
    {
        if (waveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("Mixer wave format must be IEEE float");

        this.radioId = radioId;
        sources = new List<ClientAudioProvider>();
        WaveFormat = waveFormat;
        openFreqPipeline = new OpenFreqReceiverEffectsPipeline(radioId, waveFormat.SampleRate);

        //5 seconds worth of buffer
        effectsBuffer = new CircularFloatBuffer(WaveFormat.SampleRate * 5);
        _cachedAudioEffectsProvider = CachedAudioEffectProvider.Instance;
        ;

        //   waveWriter = new NAudio.Wave.WaveFileWriter($@"C:\\temp\\output{Guid.NewGuid()}.wav", new WaveFormat(AudioManager.OUTPUT_SAMPLE_RATE, 2));
    }

    /// <summary>
    ///     The output WaveFormat of this sample provider
    /// </summary>
    public WaveFormat WaveFormat { get; }

    /// <summary>
    ///     Reads samples from this sample provider
    /// </summary>
    /// <param name="buffer">Sample buffer</param>
    /// <param name="offset">Offset into sample buffer</param>
    /// <param name="count">Number of samples required</param>
    /// <returns>Number of samples read</returns>
    public int Read(float[] buffer, int offset, int count)
    {
        var floatPool = ArrayPool<float>.Shared;
        // Accumulate in local mono buffer before switching to stereo.
        //ask for count/2 as the source is MONO but the request for this is STEREO
        var monoBufferLength = count / 2; // Array pool can give us an array that is larger.
        var monoBuffer = floatPool.Rent(monoBufferLength);
       
        Array.Clear(monoBuffer, 0, monoBufferLength);

        // Read effects.
        var monoOffset = ReadEffects(monoBuffer, 0, monoBufferLength);

        // Read any available audio that we have queued up.
        monoOffset += ReadMixBuffer(monoBuffer, monoOffset, monoBufferLength - monoOffset);

        // Are we starved? Rehydrate.
        if (monoOffset < monoBufferLength)
        {
            List<TransmissionSegment> segments = null; 

            // Update sources by queueing incoming audio.
            var ky58Tone = false;
            var longestSegmentLength = 0;
            lock (sources)
            {
                if (sources.Count > 0)
                {
                    segments = new(sources.Count);
                    var index = sources.Count - 1;
                    var desired = monoBufferLength - monoOffset;
                    while (index >= 0)
                    {
                        var source = sources[index];

                        // #TODO: Should run TX effect chain per ClientAudioProvider, then mixdown.
                        // Read from the source, which should dejitter + transform the audio.
                        // Then have this radio run its mixer.
                        try
                        {
                            var segment = source.Read(radioId, desired);
                            if (segment != null)
                            {
                                segments.Add(segment);
                                longestSegmentLength = Math.Max(longestSegmentLength, segment.Audio.Length);
                                if (segment.Decryptable && segment.HasEncryption)
                                {
                                    ky58Tone = true;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            logger.Error("Error reading from source: ", e);
                        }


                        index--;
                    }
                }
                
            }

            var hasIncomingAudio = segments?.Count > 0;

            //copy to the recording service - as we have everything we need to know about the audio
            //at this point
            if (hasIncomingAudio)
            {
                _audioRecordingManager.AppendClientAudio(radioId, segments);
            }

            RefreshReceiverEngine();
            var openFreqAmOnly = false;
            var openFreqRxActive = false;
            var hasFallback = false;

            // Queue new audio (if any), or continue a short OpenFreq post-TX drain.
            if (hasIncomingAudio)
            {
                try
                {
                    // Need to be able to hold whatever is left over + incoming audio.
                    var totalSize = availableInBuffer + longestSegmentLength;
                    if (mixBuffer == null || mixBuffer.Length < totalSize)
                    {
                        Array.Resize(ref mixBuffer, totalSize);
                    }

                    var targetSpan = mixBuffer.AsSpan(availableInBuffer, longestSegmentLength);
                    targetSpan.Clear();

                    // Cache receiving-radio volume for tone scaling (same knob for every segment).
                    _radioToneVolume = segments[0].Volume;

                    List<TransmissionSegment> openFreqAm = null;
                    List<TransmissionSegment> fallback = null;
                    if (_receiverEffectsEngine == ReceiverEffectsEngine.OpenFreq)
                    {
                        openFreqAm = new List<TransmissionSegment>(segments.Count);
                        fallback = new List<TransmissionSegment>(segments.Count);
                        foreach (var seg in segments)
                        {
                            if (OpenFreqReceiverEffectsPipeline.IsEligibleOpenFreqAm(seg))
                                openFreqAm.Add(seg);
                            else
                                fallback.Add(seg);
                        }
                    }
                    else
                    {
                        fallback = segments;
                    }

                    hasFallback = fallback != null && fallback.Count > 0;
                    var hasOpenFreqAm = openFreqAm != null && openFreqAm.Count > 0;
                    openFreqAmOnly = hasOpenFreqAm && !hasFallback;

                    if (hasOpenFreqAm)
                    {
                        EnsureScratch(ref _openFreqScratch, longestSegmentLength);
                        var amSpan = _openFreqScratch.AsSpan(0, longestSegmentLength);
                        amSpan.Clear();
                        openFreqPipeline.ProcessSegments(amSpan, openFreqAm);
                        openFreqRxActive = openFreqPipeline.WasSquelchOpen;
                        _openFreqRadioVolume = openFreqAm[0].Volume;
                        // Post-AGC, post-clip character: scale then mix into target.
                        AudioManipulationHelper.ClipArray(amSpan);
                        AudioManipulationHelper.ApplyLinearGain(amSpan, _openFreqRadioVolume);
                        MixInto(targetSpan, amSpan);

                        // Drain only when this tick is OpenFreq-AM-only (no fallback bleeding in).
                        if (openFreqAmOnly)
                        {
                            _openFreqDrainSamplesRemaining =
                                OpenFreqReceiverEffectsPipeline.DrainSampleCount(WaveFormat.SampleRate);
                            lastModulation = Modulation.AM;
                        }
                        else
                        {
                            _openFreqDrainSamplesRemaining = 0;
                        }
                    }
                    else
                    {
                        _openFreqDrainSamplesRemaining = 0;
                    }

                    if (hasFallback)
                    {
                        EnsureScratch(ref _srsScratch, longestSegmentLength);
                        var srsSpan = _srsScratch.AsSpan(0, longestSegmentLength);
                        srsSpan.Clear();
                        // #TODO: pass receiving radio model name.
                        pipeline.ProcessSegments(srsSpan, fallback, null);
                        MixInto(targetSpan, srsSpan);
                        lastModulation = fallback[fallback.Count - 1].Modulation;
                        // Fallback cancels AM drain so squelch tails do not bleed across modes.
                        _openFreqDrainSamplesRemaining = 0;
                    }

                    // Final clip after combining buckets.
                    AudioManipulationHelper.ClipArray(targetSpan);
                    availableInBuffer += targetSpan.Length;

                    // Drain newly queued audio.
                    monoOffset += ReadMixBuffer(monoBuffer, monoOffset, monoBufferLength - monoOffset);
                }
                catch (Exception e)
                {
                    logger.Error(e, "Error mixing segments");
                }
            }
            else if (_openFreqDrainSamplesRemaining > 0
                     && _receiverEffectsEngine == ReceiverEffectsEngine.OpenFreq)
            {
                try
                {
                    openFreqAmOnly = true;
                    var drainFrames = Math.Min(
                        monoBufferLength - monoOffset,
                        _openFreqDrainSamplesRemaining);
                    if (drainFrames <= 0)
                        drainFrames = Math.Min(
                            Constants.OUTPUT_SEGMENT_FRAMES,
                            _openFreqDrainSamplesRemaining);

                    var totalSize = availableInBuffer + drainFrames;
                    if (mixBuffer == null || mixBuffer.Length < totalSize)
                    {
                        Array.Resize(ref mixBuffer, totalSize);
                    }

                    var targetSpan = mixBuffer.AsSpan(availableInBuffer, drainFrames);
                    targetSpan.Clear();
                    openFreqPipeline.ProcessDrain(targetSpan);
                    AudioManipulationHelper.ClipArray(targetSpan);
                    AudioManipulationHelper.ApplyLinearGain(targetSpan, _openFreqRadioVolume);
                    availableInBuffer += targetSpan.Length;
                    _openFreqDrainSamplesRemaining = Math.Max(
                        0,
                        _openFreqDrainSamplesRemaining - targetSpan.Length);
                    openFreqRxActive = openFreqPipeline.WasSquelchOpen
                                       || _openFreqDrainSamplesRemaining > 0;
                    lastModulation = Modulation.AM;

                    monoOffset += ReadMixBuffer(monoBuffer, monoOffset, monoBufferLength - monoOffset);
                }
                catch (Exception e)
                {
                    logger.Error(e, "Error draining OpenFreq receiver");
                    _openFreqDrainSamplesRemaining = 0;
                }
            }

            // OpenFreq-AM-only: activity from squelch/drain. Otherwise SRS-style presence.
            var rxActive = openFreqAmOnly
                ? openFreqRxActive
                : hasIncomingAudio || availableInBuffer > 0 || openFreqRxActive;

            // Suppress generic RADIO_TRANS clicks only when OpenFreq AM alone owns the event.
            // KY-58 is evaluated before that suppress and remains audible (volume-scaled).
            monoOffset += HandleStartEndTones(
                rxActive,
                ky58Tone,
                openFreqAmOnly,
                monoBuffer,
                monoOffset,
                monoBufferLength - monoOffset);
        }

        // Did we consume everything? Make sure we don't keep a buffer too big.
        if (mixBuffer != null && availableInBuffer == 0)
        {
            if (mixBuffer.Length > Constants.OUTPUT_SEGMENT_FRAMES)
            {
                Array.Resize(ref mixBuffer, Constants.OUTPUT_SEGMENT_FRAMES);
            }
        }


        if (monoOffset > 0)
        {
             // We have available data, make it stereo and copy to target.
            SeparateAudio(monoBuffer, 0, monoOffset, buffer, offset, radioId);
        }

        floatPool.Return(monoBuffer);
        return monoOffset * 2; // double because of mono -> stereo.
    }

    /// <summary>
    ///     Adds a new mixer input
    /// </summary>
    /// <param name="mixerInput">Mixer input</param>
    public void AddMixerInput(ClientAudioProvider mixerInput)
    {
        // we'll just call the lock around add since we are protecting against an AddMixerInput at
        // the same time as a Read, rather than two AddMixerInput calls at the same time
        lock (sources)
        {
            sources.Add(mixerInput);
        }
    }

    public void RemoveMixerInput(ClientAudioProvider mixerInput)
    {
        lock (sources)
        {
            sources.Remove(mixerInput);
        }
    }

    /// <summary>
    ///     Removes all mixer inputs
    /// </summary>
    public void RemoveAllMixerInputs()
    {
        lock (sources)
        {
            sources.Clear();
        }
    }

    private void RefreshReceiverEngine()
    {
        var now = DateTime.Now.Ticks;
        if (TimeSpan.FromTicks(now - _lastEngineRefresh).TotalSeconds <= 3)
            return;

        _lastEngineRefresh = now;
        var previous = _receiverEffectsEngine;
        var raw = profileSettings.GetClientSettingString(ProfileSettingsKeys.ReceiverEffectsEngine);
        if (!Enum.TryParse(raw, true, out _receiverEffectsEngine))
            _receiverEffectsEngine = ReceiverEffectsEngine.SRS;

        if (previous != _receiverEffectsEngine)
        {
            openFreqPipeline.Reset();
            _openFreqRadioVolume = 1f;
            _radioToneVolume = 1f;
        }
    }

    private static void EnsureScratch(ref float[] scratch, int length)
    {
        if (scratch == null || scratch.Length < length)
            scratch = new float[length];
    }

    private static void MixInto(Span<float> target, ReadOnlySpan<float> source)
    {
        var n = Math.Min(target.Length, source.Length);
        for (var i = 0; i < n; i++)
            target[i] += source[i];
    }

    private void WriteEffectScaled(float[] samples, float volume)
    {
        if (samples == null || samples.Length == 0)
            return;

        if (volume == 1f)
        {
            effectsBuffer.Write(samples, 0, samples.Length);
            return;
        }

        if (volume == 0f)
            return;

        // Scale into a temporary copy so we do not mutate the cached effect asset.
        var scaled = new float[samples.Length];
        Array.Copy(samples, scaled, samples.Length);
        AudioManipulationHelper.ApplyLinearGain(scaled, volume);
        effectsBuffer.Write(scaled, 0, scaled.Length);
    }

    private int HandleStartEndTones(
        bool isActive,
        bool encryption,
        bool suppressGenericRadioClicks,
        float[] buffer,
        int offset,
        int count)
    {
        if (isActive ^ IsReceiving)
        {
            // State change.
            if (isActive)
            {
                // Start
                effectsBuffer.Reset(); // In case we were playing the end tone, cut it short.
                PlaySoundEffectStartReceive(encryption, lastModulation, suppressGenericRadioClicks);
                IsReceiving = true;
            }
            else
            {
                IsReceiving = false;
                // end.
                //TODO not sure about simultaneous
                //We used to have this logic https://github.com/ciribob/DCS-SimpleRadioStandalone/blob/cd8fcbf7e2b2fafcf30875fc958276e3083e0ebb/DCS-SR-Client/Network/UDPVoiceHandler.cs#L135
                //if (!radioReceivingState.IsSimultaneous)
                PlaySoundEffectEndReceive(lastModulation, suppressGenericRadioClicks);
            }
        }

        return ReadEffects(buffer, offset, count);
    }

    private int ReadEffects(float[] buffer, int offset, int count)
    {
        //read
        var outputSamples = Math.Min(effectsBuffer.Count, count);
        if (outputSamples > 0)
        {
            outputSamples = effectsBuffer.Read(buffer, offset, outputSamples);
        }

        return outputSamples;
    }
    private int ReadMixBuffer(float[] buffer, int offset, int count)
    {
        // Drain current.
        var samplesRead = Math.Min(count, availableInBuffer);
        if (samplesRead > 0)
        {
            Array.Copy(mixBuffer, 0, buffer, offset, samplesRead);
            availableInBuffer -= samplesRead;
            // Shift elements so that the data available is always at the beginning.
            Array.Copy(mixBuffer, samplesRead, mixBuffer, 0, availableInBuffer);
        }

        return samplesRead;
    }

    private void PlaySoundEffectEndReceive(Modulation modulation, bool suppressGenericRadioClicks)
    {
        if (!profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioRxEffects_End)) return;

        var midsTone = profileSettings.GetClientSettingBool(ProfileSettingsKeys.MIDSRadioEffect);
        var vol = _radioToneVolume;

        if (radioId == 0)
        {
            var effect = _cachedAudioEffectsProvider.SelectedIntercomTransmissionEndEffect;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, vol);
        }
        else if (modulation == Modulation.MIDS && midsTone)
        {
            //end receive tone for MIDS
            var effect = _cachedAudioEffectsProvider.MIDSEndTone;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, vol);
        }
        else if (suppressGenericRadioClicks)
        {
            // OpenFreq AM owns keying character via squelch; skip RADIO_TRANS_* WAVs.
        }
        else
        {
            var effect = _cachedAudioEffectsProvider.SelectedRadioTransmissionEndEffect;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, vol);
        }
    }

    public void PlaySoundEffectStartReceive(bool encrypted, Modulation modulation)
        => PlaySoundEffectStartReceive(encrypted, modulation, suppressGenericRadioClicks: false);

    private void PlaySoundEffectStartReceive(
        bool encrypted,
        Modulation modulation,
        bool suppressGenericRadioClicks)
    {
        if (!profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioRxEffects_Start)) return;

        var midsTone = profileSettings.GetClientSettingBool(ProfileSettingsKeys.MIDSRadioEffect);
        var vol = _radioToneVolume;

        if (modulation == Modulation.MIDS && midsTone)
            //no tone for MIDS
            return;


        if (radioId == 0)
        {
            var effect = _cachedAudioEffectsProvider.SelectedIntercomTransmissionStartEffect;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, vol);
        }
        else if (encrypted &&
                 profileSettings.GetClientSettingBool(ProfileSettingsKeys
                     .RadioEncryptionEffects))
        {
            var effect = _cachedAudioEffectsProvider.KY58EncryptionEndTone;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, vol);
        }
        else if (suppressGenericRadioClicks)
        {
            // OpenFreq AM owns keying character via squelch; skip RADIO_TRANS_* WAVs.
        }
        else
        {
            var effect = _cachedAudioEffectsProvider.SelectedRadioTransmissionStartEffect;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, vol);
        }
    }

    public void PlaySoundEffectStartTransmit(bool encrypted, float volume, Modulation modulation)
    {
        lastModulation = modulation;
        _radioToneVolume = volume;

        if (!profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioTxEffects_Start)) return;

        var midsTone = profileSettings.GetClientSettingBool(ProfileSettingsKeys.MIDSRadioEffect);

        if (radioId == 0)
        {
            var effect = _cachedAudioEffectsProvider.SelectedIntercomTransmissionStartEffect;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, volume);
        }
        else if (encrypted && profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioEncryptionEffects))
        {
            var effect = _cachedAudioEffectsProvider.KY58EncryptionTransmitTone;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, volume);
        }
        else if (modulation == Modulation.MIDS && midsTone)
        {
            var effect = _cachedAudioEffectsProvider.MIDSTransmitTone;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, volume);
        }
        else
        {
            var effect = _cachedAudioEffectsProvider.SelectedRadioTransmissionStartEffect;

            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, volume);
        }
    }

    public void PlaySoundEffectEndTransmit(float volume, Modulation modulation)
    {
        lastModulation = modulation;
        _radioToneVolume = volume;

        if (!profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioTxEffects_End)) return;

        var midsTone = profileSettings.GetClientSettingBool(ProfileSettingsKeys.MIDSRadioEffect);

        if (radioId == 0)
        {
            var effect = _cachedAudioEffectsProvider.SelectedIntercomTransmissionEndEffect;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, volume);
        }
        else if (modulation == Modulation.MIDS && midsTone)
        {
            var effect = _cachedAudioEffectsProvider.MIDSEndTone;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, volume);
        }
        else
        {
            var effect = _cachedAudioEffectsProvider.SelectedRadioTransmissionEndEffect;
            if (effect.Loaded) WriteEffectScaled(effect.AudioEffectFloat, volume);
        }
    }


    public void SeparateAudio(float[] srcFloat, int srcOffset, int srcCount, float[] dstFloat, int dstOffset,
        int radioId)
    {
        ProfileSettingsKeys? settingType = null;
        switch (radioId)
        {
            case 0:
                settingType = ProfileSettingsKeys.IntercomChannel;
                break;
            case 1:
                settingType = ProfileSettingsKeys.Radio1Channel;
                break;
            case 2:
                settingType = ProfileSettingsKeys.Radio2Channel;
                break;
            case 3:
                settingType = ProfileSettingsKeys.Radio3Channel;
                break;
            case 4:
                settingType = ProfileSettingsKeys.Radio4Channel;
                break;
            case 5:
                settingType = ProfileSettingsKeys.Radio5Channel;
                break;
            case 6:
                settingType = ProfileSettingsKeys.Radio6Channel;
                break;
            case 7:
                settingType = ProfileSettingsKeys.Radio7Channel;
                break;
            case 8:
                settingType = ProfileSettingsKeys.Radio8Channel;
                break;
            case 9:
                settingType = ProfileSettingsKeys.Radio9Channel;
                break;
            case 10:
                settingType = ProfileSettingsKeys.Radio10Channel;
                break;
        };

        float balance = 0f;
        if (settingType.HasValue)
        {
            try
            {
                balance = profileSettings.GetClientSettingFloat(settingType.Value);
            }
            catch (Exception)
            {
                //ignore
            }
        }

        CreateBalancedMix(srcFloat, srcOffset, srcCount, dstFloat, dstOffset, balance);
    }

    public static void CreateBalancedMix(float[] srcFloat, int srcOffset, int srcCount, float[] dstFloat,
        int dstOffset, float balance)
    {
        balance = Math.Clamp(balance, -1f, 1f);
        var left = (1.0f - balance) / 2.0f;
        var right = 1.0f - left;

        //temp set of mono floats
        for (var i = 0; i < srcCount; ++i)
        {
            dstFloat[dstOffset + 2 * i] = srcFloat[srcOffset + i] * left;
            dstFloat[dstOffset + 2 * i + 1] = srcFloat[srcOffset + i] * right;
        }
    }
}