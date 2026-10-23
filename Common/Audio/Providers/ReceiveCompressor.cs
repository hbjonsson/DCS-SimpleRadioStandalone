using System;
using OpenFreqAudio;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;

/// <summary>
/// Receive audio compressor (MIL-R-29583 3.5.1.15.4.5.8): begins acting between 25% and 35% modulation
/// so high modulation doesn't clip the output. The caller's trim then serves as makeup gain.
/// </summary>
internal sealed class ReceiveCompressor
{
    private const float Ratio = 3f;

    private readonly float _threshold;
    private readonly AttackDecayFilter _envelope;

    public ReceiveCompressor(float threshold, int sampleRate)
    {
        _threshold = threshold;
        // Slow enough not to distort a steady tone (3.5.1.15.4.5.4), fast enough to catch syllables.
        _envelope = AttackDecayFilter.MakeAttackDecayFilter(0.005, 0.15, sampleRate);
    }

    public void Process(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var level = _envelope.Apply(MathF.Abs(samples[i]));
            if (level > _threshold)
                samples[i] *= MathF.Pow(_threshold / level, 1f - 1f / Ratio);
        }
    }

    public void Reset() => _envelope.D1 = 0f;
}
