using System;
using System.Collections.Generic;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Settings;
using NLog;
using OpenFreqAudio;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;

/// <summary>
/// Optional AM receive effects engine that runs OpenFreq's BASS-free
/// <see cref="RadioReceiverProcessor"/> locally. Does not alter SRS wire formats.
/// One instance is owned per <see cref="RadioMixingProvider"/> (per receiving radio).
/// </summary>
public sealed class OpenFreqReceiverEffectsPipeline
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly int _radioId;
    private readonly int _sampleRate;
    private RadioReceiverProcessor _processor;
    private long _lastRefresh;
    private float _radioEffectRatio = 1.0f;

    public OpenFreqReceiverEffectsPipeline(int radioId, int sampleRate)
    {
        _radioId = radioId;
        _sampleRate = sampleRate > 0 ? sampleRate : Constants.OUTPUT_SAMPLE_RATE;
        // Default VHF until the first segment provides a frequency.
        _processor = new RadioReceiverProcessor(_sampleRate, 125000);
    }

    /// <summary>
    /// Process AM segments through OpenFreq receiver DSP into <paramref name="audioOut"/>.
    /// Returns samples written. Non-AM / bypass segments should not call this.
    /// </summary>
    public int ProcessSegments(Span<float> audioOut, IReadOnlyList<TransmissionSegment> segments)
    {
        RefreshSettings();

        if (segments == null || segments.Count == 0)
        {
            audioOut.Clear();
            return 0;
        }

        // Tune from the first usable segment frequency (Hz → kHz).
        var freqHz = segments[0].Frequency;
        if (freqHz > 1)
        {
            var freqKhz = (int)Math.Round(freqHz / 1000.0);
            if (freqKhz > 0)
                _processor.SetFrequency(freqKhz);
        }

        var inputs = new RadioReceiverInput[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            // Map SRS distance/LOS loss (0..1 attenuating) into a usable SNR band.
            // ReceivingPower closer to 0 = stronger; near 1 = very weak.
            var loss = Math.Clamp(seg.ReceivingPower, 0.0, 0.95);
            var snrDb = RadioReceiverProcessor.DefaultSnrDb * (float)(1.0 - loss);

            inputs[i] = new RadioReceiverInput
            {
                Samples = seg.Audio,
                RelativePower = RadioReceiverProcessor.SnrDbToRelativePower(Math.Max(snrDb, 3f)),
                // v1: no PPM / beat simulation — same carrier for all.
                CarrierOffsetHz = 0f,
            };
        }

        var wet = new float[audioOut.Length];
        try
        {
            _processor.Process(wet.AsSpan(0, audioOut.Length), inputs);
        }
        catch (Exception e)
        {
            Logger.Error(e, "OpenFreq receiver DSP failed on radio {RadioId}", _radioId);
            _processor.ResetAgc();
            audioOut.Clear();
            return 0;
        }

        // Wet/dry mix using the existing RadioEffectsRatio setting.
        var wetGain = _radioEffectRatio;
        var dryGain = 1.0f - wetGain;

        if (dryGain > 0f)
        {
            // Build a simple dry mix of the input segments.
            audioOut.Clear();
            foreach (var seg in segments)
            {
                var src = seg.Audio.AsSpan();
                var n = Math.Min(src.Length, audioOut.Length);
                for (var i = 0; i < n; i++)
                    audioOut[i] += src[i];
            }
        }
        else
        {
            audioOut.Clear();
        }

        for (var i = 0; i < audioOut.Length; i++)
            audioOut[i] = audioOut[i] * dryGain + wet[i] * wetGain;

        return audioOut.Length;
    }

    public void Reset() => _processor.ResetAgc();

    /// <summary>True when every segment is a normal AM receive that OpenFreq DSP should handle.</summary>
    public static bool CanProcess(IReadOnlyList<TransmissionSegment> segments)
    {
        if (segments == null || segments.Count == 0)
            return false;

        foreach (var seg in segments)
        {
            if (seg.NoAudioEffects)
                return false;
            if (seg.Modulation != Modulation.AM)
                return false;
            if (!seg.Decryptable)
                return false;
        }

        return true;
    }

    private void RefreshSettings()
    {
        var now = DateTime.Now.Ticks;
        if (TimeSpan.FromTicks(now - _lastRefresh).TotalSeconds <= 3)
            return;

        _lastRefresh = now;
        var profile = GlobalSettingsStore.Instance.ProfileSettingsStore;
        _radioEffectRatio = Math.Clamp(
            profile.GetClientSettingFloat(ProfileSettingsKeys.RadioEffectsRatio), 0f, 1f);
    }
}
