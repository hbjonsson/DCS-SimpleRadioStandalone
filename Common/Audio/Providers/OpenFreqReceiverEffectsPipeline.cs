using System;
using System.Collections.Generic;
using System.Threading;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Recording;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Utility;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using NLog;
using OpenFreqAudio;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;

/// <summary>
/// Receive side of every radio, run locally on the listening client; wire formats are unchanged.
/// One instance per receiving radio (<see cref="RadioMixingProvider"/>), holding one receiver per kind:
/// <list type="bullet">
/// <item>AM and HAVEQUICK: OpenFreq's <see cref="RadioReceiverProcessor"/> (AN/ARC-210 AM).</item>
/// <item>FM and SINCGARS: <see cref="FmReceiverProcessor"/> (AN/ARC-210 FM).</item>
/// <item>Encrypted voice, MIDS and SATCOM: 16 kbit/s CVSD digital voice, garbling with bit errors.</item>
/// <item>Intercom: wired, wider band, no RF noise.</item>
/// </list>
/// Each radio kind ends in the receive audio compressor (MIL-R-29583 3.5.1.15.4.5.8), and every kind in a
/// trim that brings it to the same listening level. The transmitting side lives in <see cref="TransmitterProcessor"/>.
/// </summary>
public sealed class OpenFreqReceiverEffectsPipeline
{
    public enum ReceiverKind
    {
        Am,
        Fm,
        Digital,
        Intercom
    }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly ThreadLocal<Random> ThreadRng =
        new(() => new Random(Environment.TickCount * Thread.CurrentThread.ManagedThreadId));

    /// <summary>Post-TX drain so AGC, squelch and the FM squelch tail fall naturally.</summary>
    public const int DrainMilliseconds = 200;

    // Compressor thresholds: detected level at 30% AM / ±2.4 kHz FM deviation (25-35% of full scale).
    // The AM detector's AGC makes its output non-linear, hence the lower AM value.
    internal const float AmCompressorThreshold = 0.25f;
    internal const float CompressorThreshold = 0.28f;

    // Makeup after the compressor: voice peaks ~1 dB under full scale; the output limiter catches the rest.
    // The other trims match each kind's speech level to AM (3.5.1.15.4.5.7: within 2 dB across modes).
    internal const float AmOutputTrim = 1.84f;
    internal const float FmOutputTrim = 1.5f;
    internal const float DigitalOutputTrim = 1.7f;
    internal const float IntercomOutputTrim = 1f;

    private readonly int _radioId;
    private readonly int _sampleRate;
    private readonly RadioReceiverProcessor _am;
    private readonly FmReceiverProcessor _fm;
    private readonly AudioBandFilter _digitalBand;
    private readonly AudioBandFilter _intercomBand;
    private readonly ReceiveCompressor _amCompressor;
    private readonly ReceiveCompressor _fmCompressor;
    private readonly ReceiveCompressor _digitalCompressor;
    // Syllable onsets the compressor is too slow for would otherwise hard-clip in the mixer.
    private readonly PeakLimiter _limiter;
    private readonly Dictionary<string, CvsdVoiceCodec> _cvsdByGuid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _ppmByGuid = new(StringComparer.Ordinal);
    private readonly List<TransmissionSegment>[] _byKind =
        { new(), new(), new(), new() };
    private float[] _scratch = Array.Empty<float>();

    // Event-driven RF fading (OpenFreq RadioEffect style).
    private int _dropoutSamplesLeft;
    private int _dropoutFadeSamples;
    private int _dropoutInitialSamples;
    private int _deepFadeSamplesLeft;
    private int _deepFadeFadeSamples;
    private int _deepFadeInitialSamples;

    public OpenFreqReceiverEffectsPipeline(int radioId, int sampleRate)
    {
        _radioId = radioId;
        _sampleRate = sampleRate > 0 ? sampleRate : Constants.OUTPUT_SAMPLE_RATE;
        // Default VHF until the first segment provides a frequency.
        _am = new RadioReceiverProcessor(_sampleRate, 125000) { SquelchLevel = 1.0f };
        _fm = new FmReceiverProcessor(_sampleRate);
        // Digital voice leaves the KY-58/terminal into the same 300-3000 Hz radio audio stage.
        _digitalBand = new AudioBandFilter(_sampleRate, 290, 7, 3000, 6);
        // Assumption, no spec found: aircraft intercom is wired and passes roughly 250-6000 Hz.
        _intercomBand = new AudioBandFilter(_sampleRate, 250, 2, 6000, 4);
        _amCompressor = new ReceiveCompressor(AmCompressorThreshold, _sampleRate);
        _fmCompressor = new ReceiveCompressor(CompressorThreshold, _sampleRate);
        _digitalCompressor = new ReceiveCompressor(CompressorThreshold, _sampleRate);
        _limiter = new PeakLimiter(_sampleRate);
    }

    /// <summary>
    /// True if the last process call produced keyed audio: an analog squelch was open, or digital or
    /// intercom audio was present.
    /// </summary>
    public bool WasSquelchOpen { get; private set; }

    public static ReceiverKind KindOf(Modulation modulation, bool encrypted)
    {
        if (modulation == Modulation.INTERCOM)
            return ReceiverKind.Intercom;
        if (encrypted || modulation is Modulation.MIDS or Modulation.SATCOM)
            return ReceiverKind.Digital;
        if (modulation is Modulation.FM or Modulation.SINCGARS)
            return ReceiverKind.Fm;
        return ReceiverKind.Am;
    }

    public static ReceiverKind KindOf(TransmissionSegment segment)
        => KindOf(segment.Modulation, segment.HasEncryption);

    /// <summary>Analog receivers own keying through their squelch (open, tail, close).</summary>
    public static bool IsSquelchKeyed(TransmissionSegment segment)
        => KindOf(segment) is ReceiverKind.Am or ReceiverKind.Fm;

    /// <summary>
    /// Everything with effects allowed goes through this pipeline. Failed decrypts (static) and
    /// NoAudioEffects (server global frequencies) are mixed dry.
    /// </summary>
    public static bool IsEligible(TransmissionSegment segment)
        => segment != null && IsEligible(segment.Decryptable, segment.NoAudioEffects);

    public static bool IsEligible(bool decryptable, bool noAudioEffects)
        => decryptable && !noAudioEffects;

    /// <summary>
    /// Process segments into <paramref name="audioOut"/>. Returns samples written.
    /// Callers pass only <see cref="IsEligible(TransmissionSegment)"/> segments.
    /// </summary>
    public int ProcessSegments(Span<float> audioOut, IReadOnlyList<TransmissionSegment> segments)
    {
        audioOut.Clear();
        WasSquelchOpen = false;
        if (segments == null || segments.Count == 0)
            return 0;

        foreach (var list in _byKind)
            list.Clear();
        foreach (var segment in segments)
            _byKind[(int)KindOf(segment)].Add(segment);

        EnsureScratch(audioOut.Length);
        var scratch = _scratch.AsSpan(0, audioOut.Length);
        try
        {
            for (var kind = 0; kind < _byKind.Length; kind++)
            {
                if (_byKind[kind].Count == 0)
                    continue;

                scratch.Clear();
                WasSquelchOpen |= ProcessKind((ReceiverKind)kind, scratch, _byKind[kind]);
                for (var i = 0; i < audioOut.Length; i++)
                    audioOut[i] += scratch[i];
            }

            _limiter.Process(audioOut, 1);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Receiver DSP failed on radio {RadioId}", _radioId);
            _am.ResetAgc();
            audioOut.Clear();
            return 0;
        }

        return audioOut.Length;
    }

    /// <summary>
    /// Run the analog receivers with no transmitters so noise, AGC and squelch continue after key-up:
    /// AM closes fast, FM gives its squelch tail.
    /// </summary>
    public int ProcessDrain(Span<float> audioOut)
    {
        if (audioOut.IsEmpty)
            return 0;

        EnsureScratch(audioOut.Length);
        var scratch = _scratch.AsSpan(0, audioOut.Length);
        try
        {
            _am.Process(audioOut, Array.Empty<RadioReceiverInput>());
            _amCompressor.Process(audioOut);
            AudioManipulationHelper.ApplyLinearGain(audioOut, AmOutputTrim);

            _fm.Process(scratch, Array.Empty<RadioReceiverInput>());
            _fmCompressor.Process(scratch);
            AudioManipulationHelper.ApplyLinearGain(scratch, FmOutputTrim);
            for (var i = 0; i < audioOut.Length; i++)
                audioOut[i] += scratch[i];

            _limiter.Process(audioOut, 1);
            WasSquelchOpen = _am.WasSquelchOpen || _fm.WasSquelchOpen;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Receiver drain failed on radio {RadioId}", _radioId);
            _am.ResetAgc();
            audioOut.Clear();
            return 0;
        }

        return audioOut.Length;
    }

    public void Reset()
    {
        _am.ResetAgc();
        _am.SquelchLevel = 1.0f;
        _dropoutSamplesLeft = 0;
        _dropoutFadeSamples = 0;
        _dropoutInitialSamples = 0;
        _deepFadeSamplesLeft = 0;
        _deepFadeFadeSamples = 0;
        _deepFadeInitialSamples = 0;
        _ppmByGuid.Clear();
        _cvsdByGuid.Clear();
        _amCompressor.Reset();
        _fmCompressor.Reset();
        _digitalCompressor.Reset();
    }

    public static int DrainSampleCount(int sampleRate) =>
        Math.Max(1, sampleRate * DrainMilliseconds / 1000);

    private bool ProcessKind(ReceiverKind kind, Span<float> output, List<TransmissionSegment> segments)
    {
        switch (kind)
        {
            case ReceiverKind.Am:
            {
                TuneAm(segments[0].Frequency);
                _am.Process(output, BuildRfInputs(segments, segments[0].Frequency));
                _amCompressor.Process(output);
                AudioManipulationHelper.ApplyLinearGain(output, AmOutputTrim);
                return _am.WasSquelchOpen;
            }
            case ReceiverKind.Fm:
            {
                _fm.Process(output, BuildRfInputs(segments, segments[0].Frequency));
                _fmCompressor.Process(output);
                AudioManipulationHelper.ApplyLinearGain(output, FmOutputTrim);
                return _fm.WasSquelchOpen;
            }
            case ReceiverKind.Digital:
            {
                foreach (var segment in segments)
                {
                    var voice = (float[])segment.Audio.Clone();
                    var codec = GetCodec(segment.OriginalClientGuid ?? segment.ClientGuid);
                    codec.BitErrorRate = EstimateBitErrorRate(segment);
                    codec.Process(voice);
                    Mix(output, voice);
                }

                for (var i = 0; i < output.Length; i++)
                    output[i] = _digitalBand.Process(output[i]);
                _digitalCompressor.Process(output);
                AudioManipulationHelper.ApplyLinearGain(output, DigitalOutputTrim);
                return true;
            }
            default:
            {
                foreach (var segment in segments)
                    Mix(output, segment.Audio);
                // No RF stage, so no receive compressor; the output limiter still applies.
                for (var i = 0; i < output.Length; i++)
                    output[i] = _intercomBand.Process(output[i]);
                AudioManipulationHelper.ApplyLinearGain(output, IntercomOutputTrim);
                return true;
            }
        }
    }

    private RadioReceiverInput[] BuildRfInputs(List<TransmissionSegment> segments, double tunedHz)
    {
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
                Key = seg.OriginalClientGuid ?? seg.ClientGuid,
            };
        }

        return inputs;
    }

    private void TuneAm(double tunedHz)
    {
        // Hz -> kHz; the AM noise character follows the band.
        if (tunedHz <= 1)
            return;
        var freqKhz = (int)Math.Round(tunedHz / 1000.0);
        if (freqKhz > 0)
            _am.SetFrequency(freqKhz);
    }

    private CvsdVoiceCodec GetCodec(string guid)
    {
        guid ??= "unknown";
        if (!_cvsdByGuid.TryGetValue(guid, out var codec))
        {
            codec = new CvsdVoiceCodec(_sampleRate);
            _cvsdByGuid[guid] = codec;
        }

        return codec;
    }

    /// <summary>
    /// Bit errors for digital voice over a radio link: non-coherent FSK, Eb/N0 ~3 dB under the RF SNR.
    /// Clean at short range, garbled near the edge. MIDS and SATCOM run their own error-corrected links.
    /// </summary>
    internal static double EstimateBitErrorRate(TransmissionSegment seg)
    {
        if (seg.Modulation is Modulation.MIDS or Modulation.SATCOM)
            return 0;
        var ebN0 = Math.Pow(10, (EstimateSnrDb(seg) - 3) / 10);
        return 0.5 * Math.Exp(-ebN0 / 2);
    }

    private void EnsureScratch(int length)
    {
        if (_scratch.Length < length)
            _scratch = new float[length];
    }

    private static void Mix(Span<float> target, ReadOnlySpan<float> source)
    {
        var n = Math.Min(target.Length, source.Length);
        for (var i = 0; i < n; i++)
            target[i] += source[i];
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
