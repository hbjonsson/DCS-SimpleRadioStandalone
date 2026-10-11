using System;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Recording;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Utility;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Tests.Audio;

[TestClass]
public class ModulationRoutingAndVolumeTests
{
    private static TransmissionSegment MakeSegment(
        float[] audio,
        Modulation modulation = Modulation.AM,
        bool decryptable = true,
        bool noEffects = false,
        double receivingPower = 0.1,
        float lineOfSightLoss = 0f,
        float volume = 1f,
        string guid = "AAAAAAAAAAAAAAAAAAAAAA")
    {
        return new TransmissionSegment(new DeJitteredTransmission
        {
            PCMMonoAudio = audio,
            PCMAudioLength = audio.Length,
            Modulation = modulation,
            Decryptable = decryptable,
            NoAudioEffects = noEffects,
            Frequency = 125_000_000,
            ReceivingPower = receivingPower,
            LineOfSightLoss = lineOfSightLoss,
            Volume = volume,
            Encryption = decryptable ? (short)0 : (short)1,
            Guid = guid,
            OriginalClientGuid = guid,
        });
    }

    [TestMethod]
    public void RoutingMatrix_SegmentShape_OnlyClearAmMatches()
    {
        Assert.IsTrue(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            Modulation.AM, decryptable: true, noAudioEffects: false));

        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            Modulation.AM, decryptable: false, noAudioEffects: false));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            Modulation.AM, decryptable: true, noAudioEffects: true));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            Modulation.FM, decryptable: true, noAudioEffects: false));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            Modulation.SINCGARS, decryptable: true, noAudioEffects: false));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            Modulation.HAVEQUICK, decryptable: true, noAudioEffects: false));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            Modulation.MIDS, decryptable: true, noAudioEffects: false));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            Modulation.SATCOM, decryptable: true, noAudioEffects: false));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            Modulation.INTERCOM, decryptable: true, noAudioEffects: false));
    }

    [TestMethod]
    public void CanProcess_RejectsMixedAmAndFmBatch()
    {
        var am = MakeSegment(new float[32], Modulation.AM);
        var fm = MakeSegment(new float[32], Modulation.FM);
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.CanProcess(new[] { am, fm }));
    }

    [TestMethod]
    public void CanProcess_AcceptsDecryptableEncryptedAm()
    {
        // Successful KY-58 decrypt is still clear AM for OpenFreq DSP.
        var seg = new TransmissionSegment(new DeJitteredTransmission
        {
            PCMMonoAudio = new float[16],
            PCMAudioLength = 16,
            Modulation = Modulation.AM,
            Decryptable = true,
            Encryption = 3,
            Frequency = 251_000_000,
            Volume = 1f,
            Guid = "AAAAAAAAAAAAAAAAAAAAAA",
            OriginalClientGuid = "AAAAAAAAAAAAAAAAAAAAAA",
        });
        Assert.IsTrue(OpenFreqReceiverEffectsPipeline.CanProcess(new[] { seg }));
        Assert.IsTrue(seg.HasEncryption);
        Assert.IsTrue(seg.Decryptable);
    }

    [TestMethod]
    public void ConvertRadioVolumeSlider_KnownPoints()
    {
        Assert.AreEqual(1f, VolumeConversionHelper.ConvertRadioVolumeSlider(1f), 1e-5f);
        Assert.AreEqual(0.5f, VolumeConversionHelper.ConvertRadioVolumeSlider(0.5f), 1e-5f);
        // Near-mute: tiny but non-zero linear gain.
        var mute = VolumeConversionHelper.ConvertRadioVolumeSlider(0f);
        Assert.IsTrue(mute > 0f && mute < 0.001f);
    }

    [TestMethod]
    public void CreateBalancedMix_LeftCenterRight()
    {
        var mono = new float[] { 1f, 0.5f, -0.25f };
        var stereo = new float[mono.Length * 2];

        RadioMixingProvider.CreateBalancedMix(mono, 0, mono.Length, stereo, 0, balance: -1f);
        Assert.AreEqual(1f, stereo[0], 1e-5f);
        Assert.AreEqual(0f, stereo[1], 1e-5f);

        RadioMixingProvider.CreateBalancedMix(mono, 0, mono.Length, stereo, 0, balance: 0f);
        Assert.AreEqual(0.5f, stereo[0], 1e-5f);
        Assert.AreEqual(0.5f, stereo[1], 1e-5f);

        RadioMixingProvider.CreateBalancedMix(mono, 0, mono.Length, stereo, 0, balance: 1f);
        Assert.AreEqual(0f, stereo[0], 1e-5f);
        Assert.AreEqual(1f, stereo[1], 1e-5f);
    }

    [TestMethod]
    public void FmCapture_SelectsLowestCombinedLoss()
    {
        Assert.IsTrue(ClientEffectsPipeline.IsFmCaptureModulation(Modulation.FM));
        Assert.IsTrue(ClientEffectsPipeline.IsFmCaptureModulation(Modulation.SINCGARS));
        Assert.IsFalse(ClientEffectsPipeline.IsFmCaptureModulation(Modulation.HAVEQUICK));
        Assert.IsFalse(ClientEffectsPipeline.IsFmCaptureModulation(Modulation.AM));

        var strong = MakeSegment(new float[8], Modulation.FM, receivingPower: 0.05, lineOfSightLoss: 0f, guid: "STRONG");
        var weak = MakeSegment(new float[8], Modulation.FM, receivingPower: 0.9, lineOfSightLoss: 0.5f, guid: "WEAK");

        var strongLoss = ClientEffectsPipeline.CombinedReceiveLoss(strong);
        var weakLoss = ClientEffectsPipeline.CombinedReceiveLoss(weak);
        Assert.IsTrue(strongLoss < weakLoss, $"expected strong {strongLoss} < weak {weakLoss}");
    }

    [TestMethod]
    public void FmCapture_SincgarsUsesSameLossMetric()
    {
        var a = MakeSegment(new float[4], Modulation.SINCGARS, receivingPower: 0.2, lineOfSightLoss: 0.1f);
        var b = MakeSegment(new float[4], Modulation.SINCGARS, receivingPower: 0.8, lineOfSightLoss: 0.1f);
        Assert.IsTrue(ClientEffectsPipeline.CombinedReceiveLoss(a) < ClientEffectsPipeline.CombinedReceiveLoss(b));
    }

    [TestMethod]
    public void ApplyLinearGain_AfterClip_IsPureLoudness()
    {
        var clipped = new float[] { 1f, -1f, 0.5f };
        AudioManipulationHelper.ClipArray(clipped);
        var atHalf = (float[])clipped.Clone();
        AudioManipulationHelper.ApplyLinearGain(atHalf, 0.5f);
        CollectionAssert.AreEqual(new float[] { 0.5f, -0.5f, 0.25f }, atHalf);

        var atUnity = (float[])clipped.Clone();
        AudioManipulationHelper.ApplyLinearGain(atUnity, 1f);
        CollectionAssert.AreEqual(clipped, atUnity);
    }

    [TestMethod]
    public void PartitionEligibility_FailedDecryptAndNoEffectsStayOnFallback()
    {
        var clearAm = MakeSegment(new float[8], Modulation.AM);
        var failed = MakeSegment(new float[8], Modulation.AM, decryptable: false);
        var noFx = MakeSegment(new float[8], Modulation.AM, noEffects: true);
        var fm = MakeSegment(new float[8], Modulation.FM);

        Assert.IsTrue(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            clearAm.Modulation, clearAm.Decryptable, clearAm.NoAudioEffects));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            failed.Modulation, failed.Decryptable, failed.NoAudioEffects));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            noFx.Modulation, noFx.Decryptable, noFx.NoAudioEffects));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.HasOpenFreqAmSegmentShape(
            fm.Modulation, fm.Decryptable, fm.NoAudioEffects));
    }
}
