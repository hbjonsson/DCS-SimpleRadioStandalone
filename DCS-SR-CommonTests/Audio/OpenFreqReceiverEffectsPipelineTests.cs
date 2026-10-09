using System;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
}
