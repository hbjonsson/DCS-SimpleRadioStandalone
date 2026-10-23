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
    public void RoutingMatrix_EveryModulationHasAReceiver()
    {
        Assert.AreEqual(OpenFreqReceiverEffectsPipeline.ReceiverKind.Am,
            OpenFreqReceiverEffectsPipeline.KindOf(Modulation.AM, encrypted: false));
        Assert.AreEqual(OpenFreqReceiverEffectsPipeline.ReceiverKind.Am,
            OpenFreqReceiverEffectsPipeline.KindOf(Modulation.HAVEQUICK, encrypted: false));
        Assert.AreEqual(OpenFreqReceiverEffectsPipeline.ReceiverKind.Fm,
            OpenFreqReceiverEffectsPipeline.KindOf(Modulation.FM, encrypted: false));
        Assert.AreEqual(OpenFreqReceiverEffectsPipeline.ReceiverKind.Fm,
            OpenFreqReceiverEffectsPipeline.KindOf(Modulation.SINCGARS, encrypted: false));
        Assert.AreEqual(OpenFreqReceiverEffectsPipeline.ReceiverKind.Digital,
            OpenFreqReceiverEffectsPipeline.KindOf(Modulation.MIDS, encrypted: false));
        Assert.AreEqual(OpenFreqReceiverEffectsPipeline.ReceiverKind.Digital,
            OpenFreqReceiverEffectsPipeline.KindOf(Modulation.SATCOM, encrypted: false));
        Assert.AreEqual(OpenFreqReceiverEffectsPipeline.ReceiverKind.Intercom,
            OpenFreqReceiverEffectsPipeline.KindOf(Modulation.INTERCOM, encrypted: false));

        // KY-58 / VINSON is digital voice whatever the radio's modulation.
        Assert.AreEqual(OpenFreqReceiverEffectsPipeline.ReceiverKind.Digital,
            OpenFreqReceiverEffectsPipeline.KindOf(Modulation.AM, encrypted: true));
        Assert.AreEqual(OpenFreqReceiverEffectsPipeline.ReceiverKind.Digital,
            OpenFreqReceiverEffectsPipeline.KindOf(Modulation.FM, encrypted: true));
    }

    [TestMethod]
    public void PartitionEligibility_FailedDecryptAndNoEffectsStayOnFallback()
    {
        Assert.IsTrue(OpenFreqReceiverEffectsPipeline.IsEligible(MakeSegment(new float[8], Modulation.AM)));
        Assert.IsTrue(OpenFreqReceiverEffectsPipeline.IsEligible(MakeSegment(new float[8], Modulation.FM)));
        Assert.IsTrue(OpenFreqReceiverEffectsPipeline.IsEligible(MakeSegment(new float[8], Modulation.INTERCOM)));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.IsEligible(
            MakeSegment(new float[8], Modulation.AM, decryptable: false)));
        Assert.IsFalse(OpenFreqReceiverEffectsPipeline.IsEligible(
            MakeSegment(new float[8], Modulation.AM, noEffects: true)));
    }
}
