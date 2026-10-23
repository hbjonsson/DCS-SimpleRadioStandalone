using NAudio.Wave;
using System;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Recording;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Utility;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;

//From https://raw.githubusercontent.com/naudio/NAudio/master/NAudio/Wave/SampleProviders/VolumeSampleProvider.cs
public class VolumeSampleProviderWithPeak : ISampleProvider
{
    public delegate void SamplePeak(float peak);

    private readonly PeakLimiter _limiter;
    private readonly SamplePeak _samplePeak;
    private readonly ISampleProvider source;

    /// <summary>
    ///     Initializes a new instance of VolumeSampleProvider
    /// </summary>
    /// <param name="source">Source Sample Provider</param>
    public VolumeSampleProviderWithPeak(ISampleProvider source, SamplePeak samplePeak)
    {
        this.source = source;
        _samplePeak = samplePeak;
        _limiter = new PeakLimiter(source.WaveFormat.SampleRate);
        Volume = 1.0f;
    }

    /// <summary>
    ///     Allows adjusting the volume, 1.0f = full volume
    /// </summary>
    public float Volume { get; set; }

    /// <summary>
    ///     WaveFormat
    /// </summary>
    public WaveFormat WaveFormat => source.WaveFormat;

    /// <summary>
    ///     Reads samples from this sample provider
    /// </summary>
    /// <param name="buffer">Sample buffer</param>
    /// <param name="offset">Offset into sample buffer</param>
    /// <param name="sampleCount">Number of samples desired</param>
    /// <returns>Number of samples read</returns>
    public int Read(float[] buffer, int offset, int sampleCount)
    {
        var samplesRead = source.Read(buffer, offset, sampleCount);
        var samples = buffer.AsSpan(offset, samplesRead);

        AudioManipulationHelper.ApplyLinearGain(samples, Volume);
        //stop over boosting without crackle
        _limiter.Process(samples, WaveFormat.Channels);

        float peak = 0;
        foreach (var sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));

        _samplePeak(peak);

        return samplesRead;
    }
}