using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenFreqAudio;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Tests.Audio;

[TestClass]
public class ProceduralAmbientProcessorTests
{
    [TestMethod]
    [DataRow("f16", ProceduralAmbientProcessor.Profile.AirF16)]
    [DataRow("F15", ProceduralAmbientProcessor.Profile.AirF15)]
    [DataRow("a10", ProceduralAmbientProcessor.Profile.AirA10)]
    [DataRow("fa18", ProceduralAmbientProcessor.Profile.AirGeneric)]
    [DataRow("jet", ProceduralAmbientProcessor.Profile.AirGeneric)]
    [DataRow("ground", ProceduralAmbientProcessor.Profile.Ground)]
    [DataRow("mrap", ProceduralAmbientProcessor.Profile.Ground)]
    [DataRow("awacs", ProceduralAmbientProcessor.Profile.Stationary)]
    [DataRow("atc", ProceduralAmbientProcessor.Profile.Stationary)]
    public void TryMapAircraft_MapsSupportedJets(
        string aircraft,
        ProceduralAmbientProcessor.Profile expected)
    {
        Assert.IsTrue(ProceduralAmbientProcessor.TryMapAircraft(aircraft, out var actual));
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow("uh1")]
    [DataRow("spitfire")]
    [DataRow("hercules")]
    [DataRow("")]
    public void TryMapAircraft_LeavesUnsupportedAircraftForWavFallback(string aircraft)
    {
        Assert.IsFalse(ProceduralAmbientProcessor.TryMapAircraft(aircraft, out _));
    }

    [TestMethod]
    public void Process_ProducesFiniteChangedAudio()
    {
        var processor = new ProceduralAmbientProcessor(
            48_000,
            ProceduralAmbientProcessor.Profile.AirF16);
        var samples = new float[960];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = 0.15f * MathF.Sin(2f * MathF.PI * 700f * i / 48_000f);

        var original = (float[])samples.Clone();
        processor.Process(samples, 0.3f);

        CollectionAssert.AreNotEqual(original, samples);
        foreach (var sample in samples)
            Assert.IsTrue(float.IsFinite(sample));
    }

    [TestMethod]
    public void Process_ZeroVolume_IsBypass()
    {
        var processor = new ProceduralAmbientProcessor(
            48_000,
            ProceduralAmbientProcessor.Profile.AirGeneric);
        var samples = new[] { -0.25f, 0f, 0.25f };
        var original = (float[])samples.Clone();

        processor.Process(samples, 0f);

        CollectionAssert.AreEqual(original, samples);
    }
}
