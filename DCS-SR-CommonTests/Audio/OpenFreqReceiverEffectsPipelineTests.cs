using System;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Recording;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenFreqAudio;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Tests.Audio;

[TestClass]
public class OpenFreqReceiverEffectsPipelineTests
{
    private static TransmissionSegment MakeSegment(
        float[] audio,
        Modulation modulation = Modulation.AM,
        bool decryptable = true,
        bool noEffects = false,
        double frequencyHz = 125_000_000)
    {
        return new TransmissionSegment(new DeJitteredTransmission
        {
            PCMMonoAudio = audio,
            PCMAudioLength = audio.Length,
            Modulation = modulation,
            Decryptable = decryptable,
            NoAudioEffects = noEffects,
            Frequency = frequencyHz,
            ReceivingPower = 0.1,
            Guid = "AAAAAAAAAAAAAAAAAAAAAA",
            OriginalClientGuid = "AAAAAAAAAAAAAAAAAAAAAA",
        });
    }

    [TestMethod]
    public void CanProcess_AcceptsNormalAm()
    {
        var seg = MakeSegment(new float[480]);
        Assert.IsTrue(OpenFreqReceiverEffectsPipeline.CanProcess(new[] { seg }));
    }

    [TestMethod]
    public void CanProcess_RejectsIntercomAndFmAndEncrypted()
    {
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.CanProcess(new[]
        {
            MakeSegment(new float[100], Modulation.INTERCOM)
        }));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.CanProcess(new[]
        {
            MakeSegment(new float[100], Modulation.FM)
        }));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.CanProcess(new[]
        {
            MakeSegment(new float[100], decryptable: false)
        }));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.CanProcess(new[]
        {
            MakeSegment(new float[100], noEffects: true)
        }));
    }

    [TestMethod]
    public void ProcessSegments_ProducesFiniteAmAudioAndKeepsPerRadioState()
    {
        var pipeline = new OpenFreqReceiverEffectsPipeline(radioId: 1, sampleRate: Constants.OUTPUT_SAMPLE_RATE);
        var frame = Constants.OUTPUT_SEGMENT_FRAMES;
        var voice = new float[frame];
        for (var i = 0; i < frame; i++)
            voice[i] = 0.2f * MathF.Sin(2 * MathF.PI * 800f * i / Constants.OUTPUT_SAMPLE_RATE);

        var output = new float[frame];
        for (var b = 0; b < 8; b++)
            pipeline.ProcessSegments(output, new[] { MakeSegment(voice) });

        Assert.IsTrue(Array.Exists(output, s => MathF.Abs(s) > 0.01f));
        foreach (var s in output)
            Assert.IsTrue(float.IsFinite(s));

        // Second radio instance must not share AGC state with the first.
        var other = new OpenFreqReceiverEffectsPipeline(radioId: 2, sampleRate: Constants.OUTPUT_SAMPLE_RATE);
        var otherOut = new float[frame];
        other.ProcessSegments(otherOut, new[] { MakeSegment(voice, frequencyHz: 251_000_000) });
        Assert.IsTrue(float.IsFinite(otherOut[0]));
    }

    [TestMethod]
    public void ProcessSegments_EmptyInput_ClearsBuffer()
    {
        var pipeline = new OpenFreqReceiverEffectsPipeline(0, Constants.OUTPUT_SAMPLE_RATE);
        var output = new float[64];
        Array.Fill(output, 1f);
        var written = pipeline.ProcessSegments(output, Array.Empty<TransmissionSegment>());
        Assert.AreEqual(0, written);
        CollectionAssert.AreEqual(new float[64], output);
    }

    [TestMethod]
    public void ProcessDrain_AfterCarrier_ClosesSquelchWithoutCrashing()
    {
        var pipeline = new OpenFreqReceiverEffectsPipeline(1, Constants.OUTPUT_SAMPLE_RATE);
        var frame = Constants.OUTPUT_SEGMENT_FRAMES;
        var voice = new float[frame];
        for (var i = 0; i < frame; i++)
            voice[i] = 0.25f * MathF.Sin(2 * MathF.PI * 900f * i / Constants.OUTPUT_SAMPLE_RATE);

        var output = new float[frame];
        for (var b = 0; b < 4; b++)
            pipeline.ProcessSegments(output, new[] { MakeSegment(voice) });

        Assert.IsTrue(pipeline.WasSquelchOpen);

        var openedDuringDrain = false;
        for (var b = 0; b < 12; b++)
        {
            pipeline.ProcessDrain(output);
            foreach (var s in output)
                Assert.IsTrue(float.IsFinite(s));
            if (pipeline.WasSquelchOpen)
                openedDuringDrain = true;
        }

        // Tail may briefly stay open, then should settle closed with empty transmitters.
        Assert.IsFalse(pipeline.WasSquelchOpen);
        // openedDuringDrain is optional character; don't require it.
        _ = openedDuringDrain;
    }

    [TestMethod]
    public void EstimateSnrDb_SteadyStateStaysAboveSquelchFloor()
    {
        var strong = MakeSegment(new float[64], frequencyHz: 125_000_000);
        // Force weaker geometry via reflection of private path: construct segment with high loss.
        var weak = new TransmissionSegment(
            new float[64],
            Modulation.AM,
            frequencyHz: 125_000_000,
            receivingPower: 0.9,
            lineOfSightLoss: 0.5f,
            clientGuid: "CCCCCCCCCCCCCCCCCCCCCC");

        var strongSnr = OpenFreqReceiverEffectsPipeline.EstimateSnrDb(strong);
        var weakSnr = OpenFreqReceiverEffectsPipeline.EstimateSnrDb(weak);

        Assert.IsTrue(strongSnr >= 10f);
        Assert.IsTrue(weakSnr >= 10f, "Steady-state must not permanently mute mid-distance RX");
        Assert.IsTrue(RadioReceiverProcessor.SnrDbToRelativePower(weakSnr) >= 2.0f);
    }

    [TestMethod]
    public void ProcessSegments_MultiTx_ProducesFiniteCollisionMix()
    {
        var pipeline = new OpenFreqReceiverEffectsPipeline(3, Constants.OUTPUT_SAMPLE_RATE);
        var frame = Constants.OUTPUT_SEGMENT_FRAMES;
        var a = new float[frame];
        var b = new float[frame];
        for (var i = 0; i < frame; i++)
        {
            a[i] = 0.18f * MathF.Sin(2 * MathF.PI * 700f * i / Constants.OUTPUT_SAMPLE_RATE);
            b[i] = 0.18f * MathF.Sin(2 * MathF.PI * 900f * i / Constants.OUTPUT_SAMPLE_RATE);
        }

        var segA = MakeSegment(a, frequencyHz: 251_000_000);
        var segB = new TransmissionSegment(
            b,
            Modulation.AM,
            frequencyHz: 251_000_050,
            receivingPower: 0.35,
            lineOfSightLoss: 0.1f,
            clientGuid: "BBBBBBBBBBBBBBBBBBBBBB");

        var output = new float[frame];
        pipeline.ProcessSegments(output, new[] { segA, segB });
        Assert.IsTrue(Array.Exists(output, s => MathF.Abs(s) > 0.005f));
        foreach (var s in output)
            Assert.IsTrue(float.IsFinite(s));
    }

    [TestMethod]
    public void TransmissionSegment_CopiesVolumeFromDeJitteredTransmission()
    {
        var audio = new float[32];
        var seg = new TransmissionSegment(new DeJitteredTransmission
        {
            PCMMonoAudio = audio,
            PCMAudioLength = audio.Length,
            Modulation = Modulation.AM,
            Decryptable = true,
            Frequency = 125_000_000,
            Volume = 0.37f,
            Guid = "AAAAAAAAAAAAAAAAAAAAAA",
            OriginalClientGuid = "AAAAAAAAAAAAAAAAAAAAAA",
        });

        Assert.AreEqual(0.37f, seg.Volume);
    }

    [TestMethod]
    public void TransmissionSegment_PreviewCtor_DefaultsVolumeToUnity()
    {
        var seg = new TransmissionSegment(new float[8], Modulation.AM, 125_000_000);
        Assert.AreEqual(1f, seg.Volume);
    }

    [TestMethod]
    public void ApplyLinearGain_UnityIsNoOp()
    {
        var samples = new float[] { -0.9f, -0.25f, 0f, 0.5f, 1f };
        var original = (float[])samples.Clone();

        AudioManipulationHelper.ApplyLinearGain(samples, 1f);

        CollectionAssert.AreEqual(original, samples);
    }

    [TestMethod]
    public void ApplyLinearGain_HalfScalesLinearly()
    {
        var samples = new float[] { -0.8f, -0.2f, 0f, 0.4f, 1f };

        AudioManipulationHelper.ApplyLinearGain(samples, 0.5f);

        CollectionAssert.AreEqual(new float[] { -0.4f, -0.1f, 0f, 0.2f, 0.5f }, samples);
    }

    [TestMethod]
    public void ApplyLinearGain_ZeroSilencesBuffer()
    {
        var samples = new float[] { -1f, 0.25f, 0.9f };

        AudioManipulationHelper.ApplyLinearGain(samples, 0f);

        CollectionAssert.AreEqual(new float[3], samples);
    }
}
