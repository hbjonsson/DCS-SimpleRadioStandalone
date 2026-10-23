using NWaves.Filters.Base;
using NWaves.Filters.Butterworth;
using NWaves.Filters.Fda;

namespace OpenFreqAudio;

/// <summary>Butterworth band-pass built from a high-pass and a low-pass, as second-order sections.</summary>
public sealed class AudioBandFilter
{
    private readonly FilterChain _highPass;
    private readonly LowPassFilter _lowPass;

    public AudioBandFilter(int sampleRate, double lowHz, int lowOrder, double highHz, int highOrder)
    {
        _highPass = new FilterChain(DesignFilter.TfToSos(new HighPassFilter(lowHz / sampleRate, lowOrder).Tf));
        _lowPass = new LowPassFilter(highHz / sampleRate, highOrder);
    }

    public float Process(float sample) => _lowPass.Process(_highPass.Process(sample));
}
