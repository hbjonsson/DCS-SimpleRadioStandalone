namespace OpenFreqAudio;

/// <summary>
/// Receiver audio mute with a short fade, applied after the audio filters so opening and closing
/// the squelch clicks softly instead of stepping the detector output into the filters.
/// </summary>
public sealed class SquelchGate
{
    private readonly float _coefficient;
    private float _level;

    public SquelchGate(int sampleRate, double fadeSeconds = 0.002)
    {
        _coefficient = (float)(1 - System.Math.Exp(-1 / (sampleRate * fadeSeconds)));
    }

    public float Apply(float sample, float target)
    {
        _level += (target - _level) * _coefficient;
        return sample * _level;
    }
}
