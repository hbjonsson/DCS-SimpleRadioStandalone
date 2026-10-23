using System;
using NWaves.Filters.Butterworth;

namespace OpenFreqAudio;

/// <summary>
/// 16 kbit/s continuously variable slope delta (CVSD) voice, the digital voice of TSEC/KY-58 (VINSON)
/// and Link 16 / MIDS. Encodes and decodes in one pass, with optional bit errors in between, so the output
/// carries the codec's granular noise and slope overload, and garbles as the bit error rate rises.
///
/// Encoder and decoder use the usual 3-bit coincidence step control with a 1 ms principal integrator and
/// 5 ms syllabic filter. One instance per voice stream. Not thread-safe.
/// </summary>
public sealed class CvsdVoiceCodec
{
    public const int BitRate = 16000;

    private const double PrincipalIntegratorSeconds = 0.001;
    private const double SyllabicSeconds = 0.005;
    private const float StepMax = 0.3f;
    private const float StepMin = 0.006f;
    private const double VoiceBandHz = 3400;

    private readonly int _decimation;
    private readonly LowPassFilter _antiAlias;
    private readonly LowPassFilter _reconstruction;
    private readonly float _integratorLeak;
    private readonly float _syllabicLeak;
    private readonly Channel _encoder = new();
    private readonly Channel _decoder = new();
    private int _phase;
    private float _held;
    private uint _rng = 0x2545F491u;

    /// <param name="sampleRate">Audio rate; must be a whole multiple of 16 kHz.</param>
    public CvsdVoiceCodec(int sampleRate)
    {
        if (sampleRate <= 0 || sampleRate % BitRate != 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Must be a multiple of 16 kHz.");
        _decimation = sampleRate / BitRate;
        _antiAlias = new LowPassFilter(VoiceBandHz / sampleRate, 6);
        _reconstruction = new LowPassFilter(VoiceBandHz / sampleRate, 6);
        _integratorLeak = (float)Math.Exp(-1 / (BitRate * PrincipalIntegratorSeconds));
        _syllabicLeak = (float)Math.Exp(-1 / (BitRate * SyllabicSeconds));
        _encoder.Step = _decoder.Step = StepMin;
    }

    /// <summary>Probability that a transmitted bit arrives flipped (0 = clean link).</summary>
    public double BitErrorRate { get; set; }

    public void Process(Span<float> samples)
    {
        for (var n = 0; n < samples.Length; n++)
        {
            var band = _antiAlias.Process(samples[n]);
            if (_phase == 0)
            {
                var bit = band > _encoder.Estimate;
                Update(_encoder, bit);
                if (BitErrorRate > 0 && NextUnit() < BitErrorRate)
                    bit = !bit;
                Update(_decoder, bit);
                // Zero-stuffing upsample: scale by the factor to keep the passband level.
                _held = _decoder.Estimate * _decimation;
            }
            else
            {
                _held = 0f;
            }

            samples[n] = _reconstruction.Process(_held);
            _phase = (_phase + 1) % _decimation;
        }
    }

    private void Update(Channel channel, bool bit)
    {
        channel.History = ((channel.History << 1) | (bit ? 1 : 0)) & 0b111;
        var coincidence = channel.History is 0b000 or 0b111;
        channel.Step = _syllabicLeak * channel.Step + (1 - _syllabicLeak) * (coincidence ? StepMax : StepMin);
        channel.Estimate = _integratorLeak * channel.Estimate + (bit ? channel.Step : -channel.Step);
    }

    private double NextUnit()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return _rng / 4294967296.0;
    }

    private sealed class Channel
    {
        public float Estimate;
        public float Step;
        public int History = 0b010;
    }
}
