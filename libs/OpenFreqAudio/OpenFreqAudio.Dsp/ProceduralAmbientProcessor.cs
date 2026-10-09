using System;
using System.Collections.Generic;

namespace OpenFreqAudio;

/// <summary>
/// BASS-free transmitter-side cockpit ambience ported from OpenFreq.
/// Instances are stateful and must be kept per remote transmitter/radio.
/// </summary>
public sealed class ProceduralAmbientProcessor
{
    public enum Profile
    {
        AirF16,
        AirF15,
        AirGeneric
    }

    private static readonly HashSet<string> GenericJetTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "jet", "a4", "a10", "a29", "ajs37", "av8bna", "c101", "f1", "f4",
        "f5", "f14", "f86", "f100", "fa18", "hawk", "jf17", "l39", "m2000",
        "mb339", "mig15", "mig19", "mig21", "mig29", "sk60", "su25", "su27", "t45"
    };

    private readonly int _sampleRate;
    private readonly Profile _profile;
    private readonly float _strength;

    private double _phase1;
    private double _phase2;
    private double _wobblePhase1;
    private double _wobblePhase2;
    private double _rumblePhase1;
    private double _rumblePhase2;
    private float _noiseLp;
    private float _muffleLp1;
    private float _muffleLp2;
    private uint _noiseState = 0x9E3779B9u;

    public ProceduralAmbientProcessor(int sampleRate, Profile profile, float strength = 5f)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        _sampleRate = sampleRate;
        _profile = profile;
        _strength = Math.Max(0f, strength);
    }

    /// <summary>
    /// Maps SRS/DCS ambient identifiers to procedural profiles. Unsupported aircraft
    /// deliberately return false so callers can retain SRS's WAV ambience.
    /// </summary>
    public static bool TryMapAircraft(string aircraftType, out Profile profile)
    {
        if (string.Equals(aircraftType, "f16", StringComparison.OrdinalIgnoreCase))
        {
            profile = Profile.AirF16;
            return true;
        }

        if (string.Equals(aircraftType, "f15", StringComparison.OrdinalIgnoreCase))
        {
            profile = Profile.AirF15;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(aircraftType) && GenericJetTypes.Contains(aircraftType))
        {
            profile = Profile.AirGeneric;
            return true;
        }

        profile = default;
        return false;
    }

    /// <summary>
    /// Wet/dry blends procedural cockpit acoustics into mono voice samples.
    /// </summary>
    public void Process(Span<float> samples, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        if (volume <= 0f || samples.IsEmpty)
            return;

        switch (_profile)
        {
            case Profile.AirF16:
                ProcessF16(samples, volume);
                break;
            case Profile.AirF15:
                ProcessF15(samples, volume);
                break;
            default:
                ProcessGenericJet(samples, volume);
                break;
        }
    }

    private void ProcessF16(Span<float> samples, float volume)
    {
        const float whineLevel = 0.006f;
        const float roarLevel = 0.020f;
        var muffleA = OnePoleCoefficient(1900f);
        var roarA = OnePoleCoefficient(350f);

        for (var i = 0; i < samples.Length; i++)
        {
            var wobble = Math.Sin(_wobblePhase1) * 3.0;
            Advance(ref _wobblePhase1, 0.8);

            var whine = ((float)Math.Sin(_phase1) * whineLevel
                         + (float)Math.Sin(_phase1 * 3.0) * 0.0025f
                         + (float)Math.Sin(_phase1 * 5.0) * 0.001f) * _strength;
            Advance(ref _phase1, 400.0 + wobble);

            var rumble = 1f
                         + (float)Math.Sin(_phase2) * 0.10f
                         + (float)Math.Sin(_wobblePhase2) * 0.10f;
            Advance(ref _phase2, 55.0);
            Advance(ref _wobblePhase2, 73.0);

            var roar = NextLowPassNoise(roarA);
            var dry = samples[i];
            var processed = SoftAlc(dry * rumble + whine + roar * roarLevel * _strength, 2f, 0.70f);
            var wet = TwoPoleMuffle(processed, muffleA, 0.55f);
            samples[i] = dry + volume * (wet - dry);
        }
    }

    private void ProcessF15(Span<float> samples, float volume)
    {
        var muffleA = OnePoleCoefficient(2000f);
        var roarA = OnePoleCoefficient(400f);
        var saturationNorm = 1f / MathF.Tanh(2.5f);

        for (var i = 0; i < samples.Length; i++)
        {
            var rumble = 1f
                         + (float)Math.Sin(_phase1) * 0.09f
                         + (float)Math.Sin(_phase2) * 0.07f;
            Advance(ref _phase1, 50.0);
            Advance(ref _phase2, 68.0);

            var dry = samples[i];
            var processed = dry * rumble + NextLowPassNoise(roarA) * 0.025f * _strength;
            processed = MathF.Tanh(processed * 2.5f) * saturationNorm;

            _muffleLp1 = muffleA * _muffleLp1 + (1f - muffleA) * processed;
            var wet = _muffleLp1 * 0.40f + processed * 0.60f;
            samples[i] = dry + volume * (wet - dry);
        }
    }

    private void ProcessGenericJet(Span<float> samples, float volume)
    {
        var muffleA = OnePoleCoefficient(1900f);
        var roarA = OnePoleCoefficient(350f);

        for (var i = 0; i < samples.Length; i++)
        {
            var n1Wobble = Math.Sin(_wobblePhase1) * 4.0;
            var n2Wobble = Math.Sin(_wobblePhase2) * 7.0;
            Advance(ref _wobblePhase1, 0.5);
            Advance(ref _wobblePhase2, 0.7);

            var whine = ((float)Math.Sin(_phase1) * 0.0022f
                         + (float)Math.Sin(_phase1 * 2.0) * 0.0014f
                         + (float)Math.Sin(_phase1 * 3.0) * 0.0007f
                         + (float)Math.Sin(_phase2) * 0.0018f
                         + (float)Math.Sin(_phase2 * 2.0) * 0.001f) * _strength;
            Advance(ref _phase1, 480.0 + n1Wobble);
            Advance(ref _phase2, 750.0 + n2Wobble);

            var rumble = 1f
                         + (float)Math.Sin(_rumblePhase1) * 0.09f
                         + (float)Math.Sin(_rumblePhase2) * 0.09f;
            Advance(ref _rumblePhase1, 57.0);
            Advance(ref _rumblePhase2, 76.0);
            var dry = samples[i];
            var processed = SoftAlc(
                dry * rumble + whine + NextLowPassNoise(roarA) * 0.018f * _strength,
                2f,
                0.70f);
            var wet = TwoPoleMuffle(processed, muffleA, 0.55f);
            samples[i] = dry + volume * (wet - dry);
        }
    }

    private float OnePoleCoefficient(float cutoff) =>
        MathF.Exp(-2f * MathF.PI * cutoff / _sampleRate);

    private float NextLowPassNoise(float coefficient)
    {
        _noiseState = _noiseState * 1664525u + 1013904223u;
        var raw = (int)_noiseState * (1f / 2147483648f);
        _noiseLp = coefficient * _noiseLp + (1f - coefficient) * raw;
        return _noiseLp;
    }

    private float TwoPoleMuffle(float sample, float coefficient, float filteredMix)
    {
        _muffleLp1 = coefficient * _muffleLp1 + (1f - coefficient) * sample;
        _muffleLp2 = coefficient * _muffleLp2 + (1f - coefficient) * _muffleLp1;
        return _muffleLp2 * filteredMix + sample * (1f - filteredMix);
    }

    private static float SoftAlc(float sample, float makeup, float knee)
    {
        sample *= makeup;
        var magnitude = MathF.Abs(sample);
        if (magnitude <= knee)
            return sample;

        var over = (magnitude - knee) / (1f - knee);
        return MathF.Sign(sample) * (knee + (1f - knee) * MathF.Tanh(over));
    }

    private void Advance(ref double phase, double frequency)
    {
        phase += 2.0 * Math.PI * frequency / _sampleRate;
        if (phase >= Math.PI * 2.0)
            phase -= Math.PI * 2.0;
    }
}
