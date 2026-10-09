namespace OpenFreqAudio;

/// <summary>
/// One transmitter contributing to a receiver buffer.
/// Samples are mono float PCM in [-1, 1] at the processor sample rate.
/// </summary>
public readonly struct RadioReceiverInput
{
    /// <summary>Mono voice samples for this buffer (length must match the Process output length).</summary>
    public ReadOnlyMemory<float> Samples { get; init; }

    /// <summary>
    /// Linear received power relative to the noise floor (10^(SNR_dB/20)).
    /// Larger values are louder carriers.
    /// </summary>
    public float RelativePower { get; init; }

    /// <summary>
    /// Carrier frequency offset in Hz relative to the first transmitter (0 for a single TX).
    /// Used for AM beat / multi-transmitter interference.
    /// </summary>
    public float CarrierOffsetHz { get; init; }
}
