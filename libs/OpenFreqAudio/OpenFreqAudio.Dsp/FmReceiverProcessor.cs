using System;
using System.Collections.Generic;
using NWaves.Filters.Base;
using NWaves.Filters.Butterworth;
using NWaves.Filters.Fda;

namespace OpenFreqAudio;

/// <summary>
/// Stateful narrowband FM receiver (AN/ARC-210 FM mode, MIL-R-29583 3.5.1.15).
///
/// Per buffer: every transmitter is a frequency-modulated carrier at complex baseband, summed with IF noise,
/// band-limited by the IF filter, then limited and detected by a phase discriminator. Noise quieting, the
/// threshold "clicks" of a weak signal and capture by the stronger of two carriers all follow from that.
/// A noise-operated squelch watches the discriminator output above the voice band, so a dropped carrier
/// gives the usual burst of noise until the squelch release time runs out.
///
/// One instance per receiving radio. Not thread-safe.
/// </summary>
public sealed class FmReceiverProcessor
{
    /// <summary>Deviation for full (100%) modulation. 3.5.1.15.4.5.4 pairs 90% with ±7.2 kHz.</summary>
    public const double PeakDeviationHz = 8000;

    // Narrowband IF: 3.5.1.15.2 asks for at least 17.5 kHz at the 6 dB points.
    private const double IfCutoffHz = 10000;

    // Noise squelch: discriminator noise above the voice band, open/close with hysteresis (3.5.1.15.3),
    // release 100-175 ms after the carrier drops (3.5.1.15.3.1).
    private const double SquelchNoiseCutoffHz = 5000;
    internal const float SquelchOpenNoise = 0.18f; // ~5 dB SINAD
    internal const float SquelchCloseNoise = 0.25f; // ~3 dB SINAD
    private const double SquelchReleaseSeconds = 0.12;

    private readonly int _sampleRate;
    private readonly LowPassFilter _ifI;
    private readonly LowPassFilter _ifQ;
    private readonly FilterChain _highPass;
    private readonly LowPassFilter _lowPass;
    private readonly HighPassFilter _squelchNoiseFilter;
    private readonly FirstOrderFilter _squelchNoiseLevel;
    private readonly SquelchGate _gate;
    private readonly int _releaseSamples;
    private readonly float _noiseStd;
    private readonly Dictionary<string, double> _phaseByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeKeys = new(StringComparer.Ordinal);

    private float _lastI = 1f;
    private float _lastQ;
    private bool _squelchOpen;
    private int _noisySamples;
    private uint _rng = 0x12345678u;
    private float[] _gateTarget = Array.Empty<float>();

    public FmReceiverProcessor(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _ifI = new LowPassFilter(IfCutoffHz / sampleRate, 4);
        _ifQ = new LowPassFilter(IfCutoffHz / sampleRate, 4);
        // Same 300-3000 Hz audio response as AM (3.5.1.15.4.5.1 covers both modes).
        _highPass = new FilterChain(DesignFilter.TfToSos(new HighPassFilter(290.0 / sampleRate, 7).Tf));
        _lowPass = new LowPassFilter(3100.0 / sampleRate, 6);
        _squelchNoiseFilter = new HighPassFilter(SquelchNoiseCutoffHz / sampleRate, 4);
        _squelchNoiseLevel = FirstOrderFilter.MakeFirstOrderFilter(0.005, sampleRate, 1.0);
        _gate = new SquelchGate(sampleRate);
        _releaseSamples = (int)(SquelchReleaseSeconds * sampleRate);
        // Unit noise power inside the IF bandwidth, so RelativePower^2 is the carrier-to-noise ratio.
        _noiseStd = (float)Math.Sqrt(sampleRate / (2 * IfCutoffHz) / 2);
    }

    /// <summary>True if squelch was open at any sample of the last <see cref="Process"/> call.</summary>
    public bool WasSquelchOpen { get; private set; }

    /// <summary>Smoothed discriminator noise above the voice band (diagnostics/tests).</summary>
    public float SquelchNoise => _squelchNoiseLevel.D1;

    /// <summary>Squelch disabled: hear the receiver noise (tests).</summary>
    public bool SquelchDisabled { get; set; }

    public void Process(Span<float> output, ReadOnlySpan<RadioReceiverInput> transmitters)
    {
        var samples = output.Length;
        if (_gateTarget.Length < samples)
            _gateTarget = new float[samples];

        var phases = new double[transmitters.Length];
        _activeKeys.Clear();
        for (var k = 0; k < transmitters.Length; k++)
        {
            var key = transmitters[k].Key ?? k.ToString();
            _activeKeys.Add(key);
            phases[k] = _phaseByKey.GetValueOrDefault(key);
        }

        var radiansPerHz = 2 * Math.PI / _sampleRate;
        var opened = false;
        for (var n = 0; n < samples; n++)
        {
            double i = Gaussian() * _noiseStd;
            double q = Gaussian() * _noiseStd;
            for (var k = 0; k < transmitters.Length; k++)
            {
                var tx = transmitters[k];
                var span = tx.Samples.Span;
                var m = n < span.Length ? Math.Clamp(span[n], -1f, 1f) : 0f;
                phases[k] += radiansPerHz * (tx.CarrierOffsetHz + m * PeakDeviationHz);
                i += tx.RelativePower * Math.Cos(phases[k]);
                q += tx.RelativePower * Math.Sin(phases[k]);
            }

            var fi = _ifI.Process((float)i);
            var fq = _ifQ.Process((float)q);

            // Limiter + discriminator: phase step between samples is the instantaneous frequency.
            var re = fi * _lastI + fq * _lastQ;
            var im = fq * _lastI - fi * _lastQ;
            _lastI = fi;
            _lastQ = fq;
            var audio = (float)(Math.Atan2(im, re) / radiansPerHz / PeakDeviationHz);

            var noise = _squelchNoiseFilter.Process(audio);
            var noiseLevel = MathF.Sqrt(_squelchNoiseLevel.Apply(noise * noise));
            if (noiseLevel > SquelchCloseNoise)
            {
                if (++_noisySamples >= _releaseSamples)
                    _squelchOpen = false;
            }
            else
            {
                _noisySamples = 0;
                if (noiseLevel < SquelchOpenNoise)
                    _squelchOpen = true;
            }

            var open = _squelchOpen || SquelchDisabled;
            opened |= open;
            _gateTarget[n] = open ? 1f : 0f;
            output[n] = audio;
        }

        for (var n = 0; n < samples; n++)
            output[n] = _gate.Apply(_lowPass.Process(_highPass.Process(output[n])), _gateTarget[n]);

        // Keep phases bounded and forget carriers that stopped.
        foreach (var key in new List<string>(_phaseByKey.Keys))
            if (!_activeKeys.Contains(key))
                _phaseByKey.Remove(key);
        for (var k = 0; k < transmitters.Length; k++)
            _phaseByKey[transmitters[k].Key ?? k.ToString()] = phases[k] % (2 * Math.PI);

        WasSquelchOpen = opened;
    }

    // Standard normal sample (Box-Muller over xorshift32).
    private double Gaussian()
    {
        var u1 = (NextUInt() + 1.0) / 4294967297.0;
        var u2 = NextUInt() / 4294967296.0;
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private uint NextUInt()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return _rng;
    }
}
