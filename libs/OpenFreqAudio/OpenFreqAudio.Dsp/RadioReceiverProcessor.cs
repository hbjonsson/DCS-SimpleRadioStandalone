using NWaves.Filters.Base;
using NWaves.Filters.Butterworth;
using NWaves.Filters.Fda;

namespace OpenFreqAudio;

/// <summary>
/// Stateful AM receiver DSP shared by OpenFreq playback and SRS.
///
/// Per buffer: background noise + AM envelope detection (multi-TX IQ sum) +
/// AGC + per-sample squelch + 300–3000 Hz band-pass (AN/ARC-210, MIL-R-29583 3.5.1.15.4.5.1).
///
/// One instance per receiving radio/slot. Not thread-safe — call from a single
/// audio thread and keep the instance alive across buffers so AGC/filters/noise
/// state remain continuous.
///
/// Propagation (terrain, dropout, deep fade, PPM) is intentionally out of scope;
/// callers supply fixed or derived <see cref="RadioReceiverInput.RelativePower"/>.
/// </summary>
public sealed class RadioReceiverProcessor
{
    /// <summary>AGC attack time constant (seconds). Shared with OpenFreq own-voice renderer.</summary>
    public const double AgcAttack = 0.003 / 3;

    /// <summary>AGC decay time constant (seconds). Shared with OpenFreq own-voice renderer.</summary>
    public const double AgcDecay = 0.01 / 3;

    /// <summary>Default AM modulation index (95%).</summary>
    public const double ModIndex = 0.95;

    /// <summary>Good-signal default SNR used when callers do not supply physics-derived power.</summary>
    public const float DefaultSnrDb = 25f;

    private readonly int _sampleRate;
    private readonly AttackDecayFilter _agc;
    private readonly FilterChain _highPass;
    private readonly LowPassFilter _lowPass;
    private BackgroundNoiseGenerator _noise;
    private int _frequencyKhz;
    private long _sampleNum;

    /// <summary>
    /// Squelch threshold scale. Typical OpenFreq default is 1.0 → gate opens at AGC level ≥ 2.0 (+6 dB).
    /// 0 = always open (hear noise); 1 = normal gate.
    /// </summary>
    public float SquelchLevel { get; set; } = 1.0f;

    /// <summary>True if squelch was open at any sample of the last <see cref="Process"/> call.</summary>
    public bool WasSquelchOpen { get; private set; }

    /// <summary>Tuned frequency in kHz (drives VHF vs UHF noise character).</summary>
    public int FrequencyKhz => _frequencyKhz;

    /// <summary>Current AGC delay-tap value (for diagnostics/tests).</summary>
    public float AgcLevel => _agc.D1;

    public RadioReceiverProcessor(int sampleRate, int frequencyKhz)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _frequencyKhz = frequencyKhz > 0 ? frequencyKhz : 125000;
        _agc = AttackDecayFilter.MakeAttackDecayFilter(AgcAttack, AgcDecay, sampleRate);
        // 7th order at 290 Hz meets the ARC-210 spec (300 Hz within -3 dB, >=37 dB down at 150 Hz); split into biquads because
        // a single 7th-order transfer function at 300 Hz / 48 kHz is numerically unstable.
        _highPass = new FilterChain(DesignFilter.TfToSos(new HighPassFilter(290.0 / sampleRate, 7).Tf));
        _lowPass = new LowPassFilter(3000.0 / sampleRate, 6);
        _noise = new BackgroundNoiseGenerator(sampleRate, _frequencyKhz);
    }

    /// <summary>Convert SNR in dB to linear relative power for <see cref="RadioReceiverInput"/>.</summary>
    public static float SnrDbToRelativePower(float snrDb)
        => (float)Math.Pow(10, snrDb / 20.0);

    /// <summary>Update tuned frequency (recreates noise generator when band changes).</summary>
    public void SetFrequency(int frequencyKhz)
    {
        if (frequencyKhz <= 0 || frequencyKhz == _frequencyKhz) return;
        _frequencyKhz = frequencyKhz;
        _noise = new BackgroundNoiseGenerator(_sampleRate, _frequencyKhz);
    }

    /// <summary>Reset AGC to unity (e.g. when switching effect engines).</summary>
    public void ResetAgc() => _agc.D1 = 1f;

    /// <summary>
    /// Process one mono buffer into <paramref name="output"/>.
    /// When <paramref name="transmitters"/> is empty, still advances noise/AGC/squelch
    /// (outputs silence while squelch is closed, noise when open).
    /// </summary>
    public void Process(Span<float> output, ReadOnlySpan<RadioReceiverInput> transmitters)
    {
        int samples = output.Length;
        float squelchThreshold = SquelchLevel * 2.0f;
        bool squelchOpened = false;
        int numStreams = transmitters.Length;

        if (numStreams > 0)
        {
            for (int n = 0; n < samples; ++n)
            {
                double i = _noise.NextSample();
                double q = _noise.NextSample();

                for (int k = 0; k < numStreams; ++k)
                {
                    var tx = transmitters[k];
                    var span = tx.Samples.Span;
                    float samp = n < span.Length ? span[n] : 0f;
                    double theta = 2.0 * Math.PI * tx.CarrierOffsetHz *
                                   (n + _sampleNum) / _sampleRate;
                    double a = tx.RelativePower;
                    i += a * (1 + samp * ModIndex) * Math.Cos(theta);
                    q += a * (1 + samp * ModIndex) * Math.Sin(theta);
                }

                float env = (float)Math.Sqrt(i * i + q * q);
                _agc.Apply(env);

                if (_agc.D1 >= squelchThreshold)
                {
                    output[n] = env / _agc.D1;
                    squelchOpened = true;
                }
                else
                {
                    output[n] = 0f;
                }
            }
        }
        else
        {
            for (int n = 0; n < samples; ++n)
            {
                double i = _noise.NextSample();
                double q = _noise.NextSample();
                float env = (float)Math.Sqrt(i * i + q * q);
                _agc.Apply(env);

                if (_agc.D1 >= squelchThreshold)
                {
                    output[n] = env / _agc.D1;
                    squelchOpened = true;
                }
                else
                {
                    output[n] = 0f;
                }
            }
        }

        for (int n = 0; n < samples; ++n)
        {
            output[n] = _lowPass.Process(_highPass.Process(output[n]));
        }

        WasSquelchOpen = squelchOpened;
        _sampleNum += samples;
    }
}
