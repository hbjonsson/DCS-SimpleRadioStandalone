using System;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Utility;

/// <summary>
/// Channel-linked peak limiter for the final speaker mix. Replaces a hard clamp at full scale, so
/// overlapping radios or a high speaker boost duck briefly instead of crackling.
/// </summary>
public sealed class PeakLimiter
{
    public const float Ceiling = 0.98f;

    private readonly float _release;
    private float _gain = 1f;

    public PeakLimiter(int sampleRate, double releaseSeconds = 0.1)
    {
        _release = (float)(1 - Math.Exp(-1 / (sampleRate * releaseSeconds)));
    }

    /// <summary>Limits interleaved <paramref name="samples"/> in place; output never exceeds <see cref="Ceiling"/>.</summary>
    public void Process(Span<float> samples, int channels)
    {
        for (var i = 0; i + channels <= samples.Length; i += channels)
        {
            var peak = 0f;
            for (var c = 0; c < channels; c++)
                peak = Math.Max(peak, Math.Abs(samples[i + c]));

            _gain += (1f - _gain) * _release;
            if (peak * _gain > Ceiling)
                _gain = Ceiling / peak;

            for (var c = 0; c < channels; c++)
                samples[i + c] *= _gain;
        }
    }
}
