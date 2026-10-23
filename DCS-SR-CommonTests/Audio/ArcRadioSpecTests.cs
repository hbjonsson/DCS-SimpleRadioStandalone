using System;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenFreqAudio;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Tests.Audio;

/// <summary>
/// AN/ARC-210 receive/transmit audio limits from MIL-R-29583(AS), measured the way the spec does:
/// steady 1 kHz-referenced tones, strong signal.
/// </summary>
[TestClass]
public class ArcRadioSpecTests
{
    private static readonly int Rate = Constants.OUTPUT_SAMPLE_RATE;
    private const int Frame = 960;

    // Amplitude of one frequency component (single-bin DFT over whole cycles).
    private static double ToneAmplitude(float[] x, double freq)
    {
        double re = 0, im = 0;
        for (var n = 0; n < x.Length; n++)
        {
            var ph = 2 * Math.PI * freq * n / Rate;
            re += x[n] * Math.Cos(ph);
            im += x[n] * Math.Sin(ph);
        }

        return 2 * Math.Sqrt(re * re + im * im) / x.Length;
    }

    private static float[] Sine(double freq, float amplitude, long start, int count)
    {
        var s = new float[count];
        for (var i = 0; i < count; i++)
            s[i] = amplitude * (float)Math.Sin(2 * Math.PI * freq * (start + i) / Rate);
        return s;
    }

    // Receiver alone: 30% AM tone, very strong carrier so the noise floor doesn't mask deep stopband.
    private static double ReceiverResponse(double freq)
    {
        var rx = new RadioReceiverProcessor(Rate, 251000);
        var power = RadioReceiverProcessor.SnrDbToRelativePower(100f);
        var outBuf = new float[Frame];
        var measured = new float[Rate / 5];
        long t = 0;
        for (var b = 0; b < 50 + measured.Length / Frame; b++, t += Frame)
        {
            var input = new RadioReceiverInput
            {
                Samples = Sine(freq, 0.3f / (float)RadioReceiverProcessor.ModIndex, t, Frame),
                RelativePower = power,
            };
            rx.Process(outBuf, new[] { input });
            if (b >= 50)
                Array.Copy(outBuf, 0, measured, (b - 50) * Frame, Frame);
        }

        return ToneAmplitude(measured, freq);
    }

    private static double Db(double a, double reference) => 20 * Math.Log10(a / reference);

    [TestMethod]
    public void Receiver_Passband_MeetsArc210Spec()
    {
        // MIL-R-29583 3.5.1.15.4.5.1: 300–3000 Hz within +1/−3 dB of 1 kHz,
        // ≤150 Hz at least 37 dB down, ≥8 kHz at least 25 dB down.
        var reference = ReceiverResponse(1000);
        var r150 = Db(ReceiverResponse(150), reference);
        var r300 = Db(ReceiverResponse(300), reference);
        var r3000 = Db(ReceiverResponse(3000), reference);
        var r8000 = Db(ReceiverResponse(8000), reference);
        var summary = $"150={r150:F1} 300={r300:F1} 3000={r3000:F1} 8000={r8000:F1} dB";

        Assert.IsTrue(r300 is >= -3 and <= 1, summary);
        Assert.IsTrue(r3000 is >= -3 and <= 1, summary);
        Assert.IsTrue(r150 <= -37, summary);
        Assert.IsTrue(r8000 <= -25, summary);
    }

    // Transmitter ALC + clipper alone, steady state: the modulating signal the carrier sees.
    private static float[] AlcTone(float amplitude)
    {
        var transmitter = new TransmitterProcessor(Rate);
        var measured = new float[Rate / 5];
        long t = 0;
        for (var b = 0; b < 100 + measured.Length / Frame; b++, t += Frame)
        {
            var frame = Sine(1000, amplitude, t, Frame);
            transmitter.ApplyLevelControl(frame);
            TransmitterProcessor.ApplySpeechClipper(frame, Models.Player.Modulation.AM, encrypted: false);
            if (b >= 100)
                Array.Copy(frame, 0, measured, (b - 100) * Frame, Frame);
        }

        return measured;
    }

    private static double Modulation(float[] alcOut)
    {
        var peak = 0f;
        foreach (var s in alcOut)
            peak = Math.Max(peak, Math.Abs(s));
        return peak * RadioReceiverProcessor.ModIndex;
    }

    [TestMethod]
    public void TransmitterAlc_DrivesToneToSpecModulationWithLowDistortion()
    {
        // 3.5.1.16.8.2.1: >=80% modulation at normal input; 3.5.1.16.8.2.3: <=5% harmonic distortion.
        var output = AlcTone(0.2f);
        var modulation = Modulation(output);
        var fundamental = ToneAmplitude(output, 1000);
        double harmonics = 0;
        for (var h = 2; h <= 5; h++)
            harmonics += Math.Pow(ToneAmplitude(output, 1000 * h), 2);
        var thd = Math.Sqrt(harmonics) / fundamental;
        var summary = $"modulation={modulation:P0} thd={thd:P1}";

        Assert.IsTrue(modulation is >= 0.80 and <= 1.0, summary);
        Assert.IsTrue(thd <= 0.05, summary);
    }

    [TestMethod]
    public void TransmitterAlc_QuietAndLoudSendersComeOutAlike()
    {
        var quiet = Modulation(AlcTone(0.12f));
        var loud = Modulation(AlcTone(0.9f));
        Assert.IsTrue(Math.Abs(Db(quiet, loud)) <= 2, $"quiet={quiet:P0} loud={loud:P0}");
    }

    // Receiver + receive compressor, steady 1 kHz tone at a given modulation, strong signal.
    private static float[] ReceivedTone(double modulation)
    {
        var rx = new RadioReceiverProcessor(Rate, 251000);
        var compressor = new ReceiveCompressor(OpenFreqReceiverEffectsPipeline.AmCompressorThreshold, Rate);
        var power = RadioReceiverProcessor.SnrDbToRelativePower(100f);
        var outBuf = new float[Frame];
        var measured = new float[Rate / 5];
        long t = 0;
        for (var b = 0; b < 50 + measured.Length / Frame; b++, t += Frame)
        {
            var input = new RadioReceiverInput
            {
                Samples = Sine(1000, (float)(modulation / RadioReceiverProcessor.ModIndex), t, Frame),
                RelativePower = power,
            };
            rx.Process(outBuf, new[] { input });
            compressor.Process(outBuf);
            if (b >= 50)
                Array.Copy(outBuf, 0, measured, (b - 50) * Frame, Frame);
        }

        return measured;
    }

    private static double Thd(float[] x)
    {
        double harmonics = 0;
        for (var h = 2; h <= 5; h++)
            harmonics += Math.Pow(ToneAmplitude(x, 1000 * h), 2);
        return Math.Sqrt(harmonics) / ToneAmplitude(x, 1000);
    }

    [TestMethod]
    public void ReceiveCompressor_BeginsBetween25And35PercentAm()
    {
        // 3.5.1.15.4.5.8: compressor begins to take effect between 25 and 35 percent AM.
        // Compare each level against a linear extrapolation from 20% AM.
        var a20 = ToneAmplitude(ReceivedTone(0.20), 1000);
        double Compression(double m) => Db(ToneAmplitude(ReceivedTone(m), 1000), a20 * m / 0.20);
        var at25 = Compression(0.25);
        var at35 = Compression(0.35);
        var at90 = Compression(0.90);
        var summary = $"25%={at25:F1} 35%={at35:F1} 90%={at90:F1} dB";

        Assert.IsTrue(at25 - Compression(0.20) > -0.5, summary);
        Assert.IsTrue(at35 - at25 < -0.5, summary);
        Assert.IsTrue(at90 < at35 - 3, summary);
    }

    [TestMethod]
    public void Receiver_DistortionWithinSpec()
    {
        // 3.5.1.15.4.5.4: <=5% at 30% AM, <=8% at 90% AM.
        var thd30 = Thd(ReceivedTone(0.30));
        var thd90 = Thd(ReceivedTone(0.90));
        var summary = $"30%={thd30:P1} 90%={thd90:P1}";

        Assert.IsTrue(thd30 <= 0.05, summary);
        Assert.IsTrue(thd90 <= 0.08, summary);
    }

    [TestMethod]
    public void SoftClip_IsLinearBelowKneeAndNeverExceedsFullModulation()
    {
        Assert.AreEqual(0.5f, TransmitterProcessor.SoftClip(0.5f));
        Assert.AreEqual(-0.5f, TransmitterProcessor.SoftClip(-0.5f));
        Assert.IsTrue(TransmitterProcessor.SoftClip(10f) <= 1f);
        Assert.IsTrue(TransmitterProcessor.SoftClip(-10f) >= -1f);
    }
}
