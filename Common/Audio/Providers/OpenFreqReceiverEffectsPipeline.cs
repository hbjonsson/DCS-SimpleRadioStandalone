using System;
using System.Collections.Generic;
using System.Threading;
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

    private static readonly ThreadLocal<Random> ThreadRng =
        new(() => new Random(Environment.TickCount * Thread.CurrentThread.ManagedThreadId));

    /// <summary>Post-TX drain length so AGC/squelch can fall naturally (~150 ms).</summary>
    public const int DrainMilliseconds = 150;

    private readonly int _radioId;
    private readonly int _sampleRate;
    private RadioReceiverProcessor _processor;

    // Event-driven RF fading (OpenFreq RadioEffect style).
    private int _dropoutSamplesLeft;
    private int _dropoutFadeSamples;
    private int _dropoutInitialSamples;
    private int _deepFadeSamplesLeft;
    private int _deepFadeFadeSamples;
    private int _deepFadeInitialSamples;

    private readonly Dictionary<string, float> _ppmByGuid = new(StringComparer.Ordinal);

    public OpenFreqReceiverEffectsPipeline(int radioId, int sampleRate)
    {
        _radioId = radioId;
        _sampleRate = sampleRate > 0 ? sampleRate : Constants.OUTPUT_SAMPLE_RATE;
        // Default VHF until the first segment provides a frequency.
        _processor = new RadioReceiverProcessor(_sampleRate, 125000)
        {
            SquelchLevel = 1.0f
        };
    }

    /// <summary>True if the DSP squelch gate was open for any sample of the last process call.</summary>
    public bool WasSquelchOpen => _processor.WasSquelchOpen;

    /// <summary>
    /// Process AM segments through OpenFreq receiver DSP into <paramref name="audioOut"/>.
    /// Returns samples written. Non-AM / bypass segments should not call this.
    /// </summary>
    public int ProcessSegments(Span<float> audioOut, IReadOnlyList<TransmissionSegment> segments)
    {
        if (segments == null || segments.Count == 0)
        {
            audioOut.Clear();
            return 0;
        }

        EnsureSquelchDefault();

        // Tune from the first usable segment frequency (Hz → kHz).
        var tunedHz = segments[0].Frequency;
        if (tunedHz > 1)
        {
            var freqKhz = (int)Math.Round(tunedHz / 1000.0);
            if (freqKhz > 0)
                _processor.SetFrequency(freqKhz);
        }

        var inputs = new RadioReceiverInput[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var samples = (float[])seg.Audio.Clone();
            var snrDb = EstimateSnrDb(seg);
            var carrierScale = ApplyEventFading(samples, snrDb);

            inputs[i] = new RadioReceiverInput
            {
                Samples = samples,
                // Deep fades scale carrier power so the gate can pop; flutter does not.
                RelativePower = RadioReceiverProcessor.SnrDbToRelativePower(snrDb) * carrierScale,
                CarrierOffsetHz = EstimateCarrierOffsetHz(seg, tunedHz),
            };
        }

        try
        {
            _processor.Process(audioOut, inputs);
        }
        catch (Exception e)
        {
            Logger.Error(e, "OpenFreq receiver DSP failed on radio {RadioId}", _radioId);
            _processor.ResetAgc();
            audioOut.Clear();
            return 0;
        }

        return audioOut.Length;
    }

    /// <summary>
    /// Run the receiver with no transmitters so noise/AGC/squelch continue after key-up.
    /// </summary>
    public int ProcessDrain(Span<float> audioOut)
    {
        if (audioOut.IsEmpty)
            return 0;

        EnsureSquelchDefault();

        try
        {
            _processor.Process(audioOut, Array.Empty<RadioReceiverInput>());
        }
        catch (Exception e)
        {
            Logger.Error(e, "OpenFreq receiver drain failed on radio {RadioId}", _radioId);
            _processor.ResetAgc();
            audioOut.Clear();
            return 0;
        }

        return audioOut.Length;
    }

    public void Reset()
    {
        _processor.ResetAgc();
        _processor.SquelchLevel = 1.0f;
        _dropoutSamplesLeft = 0;
        _dropoutFadeSamples = 0;
        _dropoutInitialSamples = 0;
        _deepFadeSamplesLeft = 0;
        _deepFadeFadeSamples = 0;
        _deepFadeInitialSamples = 0;
        _ppmByGuid.Clear();
    }

    /// <summary>
    /// True when every segment has OpenFreq-AM segment shape (clear AM, effects on).
    /// Does not read the engine setting — callers also check <see cref="IsOpenFreqEnabled"/>.
    /// Prefer partitioning with <see cref="IsEligibleOpenFreqAm(TransmissionSegment)"/> so mixed
    /// batches do not force clear AM onto the SRS mixer.
    /// </summary>
    public static bool CanProcess(IReadOnlyList<TransmissionSegment> segments)
    {
        if (segments == null || segments.Count == 0)
            return false;

        foreach (var seg in segments)
        {
            if (!HasOpenFreqAmSegmentShape(seg.Modulation, seg.Decryptable, seg.NoAudioEffects))
                return false;
        }

        return true;
    }

    /// <summary>True when the client profile selects the OpenFreq receive engine.</summary>
    public static bool IsOpenFreqEnabled()
    {
        var raw = GlobalSettingsStore.Instance.ProfileSettingsStore
            .GetClientSettingString(ProfileSettingsKeys.ReceiverEffectsEngine);
        return Enum.TryParse(raw, true, out ReceiverEffectsEngine engine)
               && engine == ReceiverEffectsEngine.OpenFreq;
    }

    /// <summary>Alias for <see cref="IsOpenFreqEnabled"/> (historical AM-focused name).</summary>
    public static bool IsOpenFreqAmEnabled() => IsOpenFreqEnabled();

    /// <summary>
    /// Segment shape for OpenFreq AM DSP: clear AM, decryptable, effects allowed.
    /// FM / MIDS / intercom / failed decrypt / NoAudioEffects never match.
    /// </summary>
    public static bool HasOpenFreqAmSegmentShape(Modulation modulation, bool decryptable, bool noAudioEffects)
        => modulation == Modulation.AM
           && decryptable
           && !noAudioEffects;

    /// <summary>
    /// Fully eligible OpenFreq AM path: engine on and segment shape matches.
    /// </summary>
    public static bool IsEligibleOpenFreqAm(Modulation modulation, bool decryptable, bool noAudioEffects)
        => IsOpenFreqEnabled()
           && HasOpenFreqAmSegmentShape(modulation, decryptable, noAudioEffects);

    public static bool IsEligibleOpenFreqAm(TransmissionSegment segment)
        => segment != null
           && IsEligibleOpenFreqAm(segment.Modulation, segment.Decryptable, segment.NoAudioEffects);

    /// <summary>
    /// Per-source gate used before SRS TX/loss processing. Requires full eligibility
    /// (not modulation alone) so failed-decrypt / NoAudioEffects AM stay on the SRS path.
    /// </summary>
    public static bool ShouldOwnAmPath(Modulation modulation, bool decryptable = true, bool noAudioEffects = false)
        => IsEligibleOpenFreqAm(modulation, decryptable, noAudioEffects);

    public static int DrainSampleCount(int sampleRate) =>
        Math.Max(1, sampleRate * DrainMilliseconds / 1000);

    private void EnsureSquelchDefault()
    {
        // Fixed OpenFreq default. Not mapped to DCS volume / cockpit knobs.
        _processor.SquelchLevel = 1.0f;
    }

    /// <summary>
    /// Steady-state SNR stays high enough that normal SRS packets open the gate
    /// (RelPower at 10 dB ≈ 3.16 &gt; threshold 2.0). Only event fades / idle drain
    /// may briefly go under squelch.
    /// </summary>
    internal static float EstimateSnrDb(TransmissionSegment seg)
    {
        // ReceivingPower: 0 = strong, approaching 1 = weak.
        // LineOfSightLoss: 0 = clear, approaching 1 = blocked.
        var distanceLoss = Math.Clamp(seg.ReceivingPower, 0.0, 0.98);
        var losLoss = Math.Clamp(seg.LineOfSightLoss, 0f, 0.98f);
        var combined = 1.0 - (1.0 - distanceLoss) * (1.0 - losLoss);
        // Map to ~10..45 dB so mid-distance never permanently mutes.
        var snrDb = 10f + (float)((1.0 - combined) * 35.0);
        return Math.Clamp(snrDb, 10f, 45f);
    }

    private float EstimateCarrierOffsetHz(TransmissionSegment seg, double tunedHz)
    {
        var freqHz = seg.Frequency > 1 ? seg.Frequency : tunedHz;
        if (freqHz <= 1)
            return 0f;

        var ppm = GetStablePpm(seg.OriginalClientGuid ?? seg.ClientGuid);
        var crystalOffset = (float)(freqHz * (ppm * 1e-6));
        var dialOffset = (float)(freqHz - tunedHz);
        return crystalOffset + dialOffset;
    }

    private float GetStablePpm(string guid)
    {
        if (string.IsNullOrEmpty(guid))
            guid = "unknown";

        if (_ppmByGuid.TryGetValue(guid, out var ppm))
            return ppm;

        // Deterministic local crystal error in roughly +/- 2.5 ppm.
        unchecked
        {
            var hash = (uint)guid.GetHashCode();
            ppm = ((hash % 5001) / 1000f) - 2.5f;
        }

        _ppmByGuid[guid] = ppm;
        return ppm;
    }

    /// <summary>
    /// OpenFreq-style Poisson flutter / deep-fade. Returns a carrier scale for
    /// <see cref="RadioReceiverInput.RelativePower"/> from deep fades only.
    /// </summary>
    private float ApplyEventFading(Span<float> samples, float snrDb)
    {
        if (samples.IsEmpty)
            return 1f;

        var rng = ThreadRng.Value!;
        ScheduleFadingEvents(rng, samples.Length, snrDb);

        var minDeepEnvelope = 1f;
        for (var i = 0; i < samples.Length; i++)
        {
            var inDrop = _dropoutSamplesLeft > 0;
            var inDeepFade = _deepFadeSamplesLeft > 0;
            var envelope = 1f;

            if (inDrop)
                envelope *= ComputeDropoutEnvelope();
            if (inDeepFade)
            {
                var deep = ComputeDeepFadeEnvelope();
                envelope *= deep;
                if (deep < minDeepEnvelope)
                    minDeepEnvelope = deep;
            }

            samples[i] *= envelope;

            if (inDrop) _dropoutSamplesLeft--;
            if (inDeepFade) _deepFadeSamplesLeft--;
        }

        return minDeepEnvelope;
    }

    private void ScheduleFadingEvents(Random rng, int frames, float snrDb)
    {
        var dropoutRate = CalculateDropoutRate(snrDb);
        var deepFadeRate = CalculateDeepFadeRate(snrDb);
        var bufferSec = (double)frames / _sampleRate;

        if (_dropoutSamplesLeft <= 0 && dropoutRate > 0.001f)
        {
            var prob = 1.0 - Math.Exp(-dropoutRate * bufferSec);
            if (rng.NextDouble() < prob)
            {
                var durMs = Math.Max(20.0, -Math.Log(1.0 - rng.NextDouble()) * 80.0);
                _dropoutSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
                _dropoutFadeSamples = (int)(_sampleRate * 8.0 / 1000.0);
                _dropoutInitialSamples = _dropoutSamplesLeft;
            }
        }

        if (_deepFadeSamplesLeft <= 0 && deepFadeRate > 0.001f)
        {
            var prob = 1.0 - Math.Exp(-deepFadeRate * bufferSec);
            if (rng.NextDouble() < prob)
            {
                var durMs = Math.Max(400.0, -Math.Log(1.0 - rng.NextDouble()) * 1200.0);
                _deepFadeSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
                _deepFadeFadeSamples = (int)(_sampleRate * 50.0 / 1000.0);
                _deepFadeInitialSamples = _deepFadeSamplesLeft;
            }
        }
    }

    private float ComputeDropoutEnvelope()
    {
        var age = _dropoutInitialSamples - _dropoutSamplesLeft;
        if (age < _dropoutFadeSamples)
            return 1f - (float)age / _dropoutFadeSamples;
        if (_dropoutSamplesLeft < _dropoutFadeSamples)
            return (float)_dropoutSamplesLeft / _dropoutFadeSamples;
        return 0f;
    }

    private float ComputeDeepFadeEnvelope()
    {
        var age = _deepFadeInitialSamples - _deepFadeSamplesLeft;
        if (age < _deepFadeFadeSamples)
            return 1f - (float)age / _deepFadeFadeSamples;
        if (_deepFadeSamplesLeft < _deepFadeFadeSamples)
            return (float)_deepFadeSamplesLeft / _deepFadeFadeSamples;
        return 0f;
    }

    // Rates mirrored from OpenFreq FastPathAudioSim (AM / longer-wavelength branch).
    private static float CalculateDropoutRate(float snrDb)
    {
        double dropout;
        if (snrDb > 15.0)
            dropout = 0.0;
        else if (snrDb > 5.0)
            dropout = (15.0 - snrDb) / 10.0 * 0.3;
        else if (snrDb > 0.0)
            dropout = 0.3 + (5.0 - snrDb) / 5.0 * 0.4;
        else
            dropout = 0.7 + Math.Min(-snrDb / 10.0, 0.5);

        return (float)(dropout * 0.7); // VHF/AM multipath factor
    }

    private static float CalculateDeepFadeRate(float snrDb)
    {
        double deepFade;
        if (snrDb > 10.0)
            deepFade = 0.0;
        else if (snrDb > 5.0)
            deepFade = (10.0 - snrDb) / 5.0 * 0.05;
        else if (snrDb > 0.0)
            deepFade = 0.05 + (5.0 - snrDb) / 5.0 * 0.10;
        else
            deepFade = 0.15 + Math.Min(-snrDb / 10.0, 0.15);

        return (float)deepFade;
    }
}
