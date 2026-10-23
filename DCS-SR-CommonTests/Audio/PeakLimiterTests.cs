using System;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Utility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Tests.Audio;

[TestClass]
public class PeakLimiterTests
{
    [TestMethod]
    public void BelowCeiling_PassesUnchanged()
    {
        var limiter = new PeakLimiter(48000);
        var samples = new[] { 0.5f, -0.9f, 0.97f, -0.2f };
        limiter.Process(samples, 2);
        CollectionAssert.AreEqual(new[] { 0.5f, -0.9f, 0.97f, -0.2f }, samples);
    }

    [TestMethod]
    public void TwoFullScaleRadiosSummed_NeverExceedCeilingAndRecover()
    {
        var limiter = new PeakLimiter(48000);
        var loud = new float[48000];
        for (var i = 0; i < loud.Length; i++)
            loud[i] = 1.8f * MathF.Sin(2 * MathF.PI * 1000f * i / 48000);
        limiter.Process(loud, 1);
        foreach (var s in loud)
            Assert.IsTrue(MathF.Abs(s) <= PeakLimiter.Ceiling + 1e-6f, $"sample {s}");

        // One second of quiet afterwards: gain returns to unity.
        var quiet = new float[48000];
        Array.Fill(quiet, 0.1f);
        limiter.Process(quiet, 1);
        Assert.AreEqual(0.1f, quiet[^1], 1e-4f);
    }

    [TestMethod]
    public void Stereo_GainIsLinkedAcrossChannels()
    {
        var limiter = new PeakLimiter(48000);
        var frame = new[] { 1.96f, 0.5f };
        limiter.Process(frame, 2);
        Assert.AreEqual(PeakLimiter.Ceiling, frame[0], 1e-6f);
        Assert.AreEqual(0.25f, frame[1], 1e-6f);
    }
}
