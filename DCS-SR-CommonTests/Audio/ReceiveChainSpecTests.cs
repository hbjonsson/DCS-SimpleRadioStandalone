using System;
using System.Collections.Generic;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenFreqAudio;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Tests.Audio;

/// <summary>
/// FM, digital voice and intercom receive paths, measured the way MIL-R-29583(AS) does where it applies:
/// steady 1 kHz tones, ±2.4 kHz deviation as the FM reference.
/// </summary>
[TestClass]
public class ReceiveChainSpecTests
{
    private static readonly int Rate = Constants.OUTPUT_SAMPLE_RATE;
    private const int Frame = 960;
    private const double Ref24KHz = 2400 / FmReceiverProcessor.PeakDeviationHz;

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

    private static double SinadDb(float[] x, double freq)
    {
        var a = ToneAmplitude(x, freq);
        double total = 0;
        foreach (var s in x)
            total += s * s;
        total /= x.Length;
        return 10 * Math.Log10(total / Math.Max(total - a * a / 2, 1e-15));
    }

    private static double Thd(float[] x, double freq)
    {
        double harmonics = 0;
        for (var h = 2; h <= 5; h++)
            harmonics += Math.Pow(ToneAmplitude(x, freq * h), 2);
        return Math.Sqrt(harmonics) / ToneAmplitude(x, freq);
    }

    private static float[] Sine(double freq, double amplitude, long start, int count)
    {
        var s = new float[count];
        for (var i = 0; i < count; i++)
            s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * freq * (start + i) / Rate));
        return s;
    }

    // FM receiver alone, steady tone, settled for 0.5 s then measured for 0.5 s.
    private static float[] FmTone(double freq, double modulation, double cnrDb, double secondCnrDb = double.NaN,
        double secondFreq = 0)
    {
        var rx = new FmReceiverProcessor(Rate);
        var outBuf = new float[Frame];
        var measured = new float[Rate / 2];
        long t = 0;
        for (var b = 0; b < 25 + measured.Length / Frame; b++, t += Frame)
        {
            var inputs = new List<RadioReceiverInput>
            {
                new()
                {
                    Samples = Sine(freq, modulation, t, Frame),
                    RelativePower = RadioReceiverProcessor.SnrDbToRelativePower((float)cnrDb),
                    Key = "A",
                },
            };
            if (!double.IsNaN(secondCnrDb))
                inputs.Add(new RadioReceiverInput
                {
                    Samples = Sine(secondFreq, modulation, t, Frame),
                    RelativePower = RadioReceiverProcessor.SnrDbToRelativePower((float)secondCnrDb),
                    CarrierOffsetHz = 150,
                    Key = "B",
                });
            rx.Process(outBuf, inputs.ToArray());
            if (b >= 25)
                Array.Copy(outBuf, 0, measured, (b - 25) * Frame, Frame);
        }

        return measured;
    }

    [TestMethod]
    public void Fm_StrongSignalQuietsAndWeakSignalIsNoisy()
    {
        // 3.5.1.15.6: (S+N)/N >= 33 dB on a strong signal. Near threshold the receiver must be audibly noisy.
        var strong = SinadDb(FmTone(1000, Ref24KHz, 35), 1000);
        var weak = SinadDb(FmTone(1000, Ref24KHz, 4), 1000);
        Assert.IsTrue(strong >= 33, $"strong={strong:F1} dB");
        Assert.IsTrue(weak < 10, $"weak={weak:F1} dB");
    }

    [TestMethod]
    public void Fm_DistortionWithinSpec()
    {
        // 3.5.1.15.4.5.4: <=5% at ±2.4 kHz, <=8% at ±7.2 kHz.
        var thd24 = Thd(FmTone(1000, Ref24KHz, 45), 1000);
        var thd72 = Thd(FmTone(1000, 7200 / FmReceiverProcessor.PeakDeviationHz, 45), 1000);
        Assert.IsTrue(thd24 <= 0.05, $"2.4 kHz: {thd24:P1}");
        Assert.IsTrue(thd72 <= 0.08, $"7.2 kHz: {thd72:P1}");
    }

    [TestMethod]
    public void Fm_PassbandMatchesArc210()
    {
        // 3.5.1.15.4.5.1 applies to FM too: 300-3000 Hz within +1/-3 dB, 150 Hz at least 37 dB down.
        double Db(double f) => 20 * Math.Log10(ToneAmplitude(FmTone(f, Ref24KHz, 60), f)
                                               / ToneAmplitude(FmTone(1000, Ref24KHz, 60), 1000));
        var r150 = Db(150);
        var r300 = Db(300);
        var r3000 = Db(3000);
        var summary = $"150={r150:F1} 300={r300:F1} 3000={r3000:F1} dB";
        Assert.IsTrue(r300 is >= -3 and <= 1, summary);
        Assert.IsTrue(r3000 is >= -3 and <= 1, summary);
        Assert.IsTrue(r150 <= -37, summary);
    }

    [TestMethod]
    public void Fm_OutputLevelHoldsFromWeakToStrong()
    {
        // 3.5.1.15.16: audio output varies no more than 3 dB from sensitivity to full signal.
        var weak = ToneAmplitude(FmTone(1000, Ref24KHz, 10), 1000);
        var strong = ToneAmplitude(FmTone(1000, Ref24KHz, 60), 1000);
        Assert.IsTrue(Math.Abs(20 * Math.Log10(weak / strong)) <= 3, $"weak={weak:F3} strong={strong:F3}");
    }

    [TestMethod]
    public void Fm_StrongerCarrierCaptures()
    {
        // Two stations on frequency, 10 dB apart: the limiter hands the audio to the stronger one.
        var mixed = FmTone(1000, Ref24KHz, 35, secondCnrDb: 25, secondFreq: 1700);
        var wanted = ToneAmplitude(mixed, 1000);
        var other = ToneAmplitude(mixed, 1700);
        Assert.IsTrue(20 * Math.Log10(wanted / other) >= 15, $"wanted={wanted:F3} other={other:F3}");
    }

    [TestMethod]
    public void Fm_SquelchOpensFastAndReleasesWithANoiseTail()
    {
        // 3.5.1.15.3.1: attack <= 30 ms, release 100-175 ms after the carrier drops.
        var rx = new FmReceiverProcessor(Rate);
        var outBuf = new float[Frame];
        var power = RadioReceiverProcessor.SnrDbToRelativePower(30f);
        var blocksToOpen = -1;
        long t = 0;
        for (var b = 0; b < 25; b++, t += Frame)
        {
            rx.Process(outBuf, new[] { new RadioReceiverInput { Samples = Sine(1000, Ref24KHz, t, Frame), RelativePower = power, Key = "A" } });
            if (blocksToOpen < 0 && rx.WasSquelchOpen)
                blocksToOpen = b + 1;
        }

        var tail = new float[Rate / 2];
        for (var b = 0; b < tail.Length / Frame; b++)
        {
            rx.Process(outBuf, Array.Empty<RadioReceiverInput>());
            Array.Copy(outBuf, 0, tail, b * Frame, Frame);
        }

        var lastAudible = Array.FindLastIndex(tail, s => MathF.Abs(s) > 1e-3f);
        var releaseMs = 1000.0 * lastAudible / Rate;
        var noiseRms = 0.0;
        for (var i = 0; i < Rate / 20; i++)
            noiseRms += tail[i] * tail[i];
        noiseRms = Math.Sqrt(noiseRms / (Rate / 20));

        Assert.IsTrue(blocksToOpen > 0 && blocksToOpen * 20 <= 30, $"opened after {blocksToOpen * 20} ms");
        Assert.IsTrue(releaseMs is >= 100 and <= 175, $"release={releaseMs:F0} ms");
        Assert.IsTrue(noiseRms > 0.05, $"tail noise rms={noiseRms:F3}");
    }

    [TestMethod]
    public void Am_SquelchOpeningDoesNotCrack()
    {
        // Squelch gates after the audio filters, so key-up cannot step the carrier DC into them.
        var rx = new RadioReceiverProcessor(Rate, 251000);
        var outBuf = new float[Frame];
        var power = RadioReceiverProcessor.SnrDbToRelativePower(40f);
        var peak = 0f;
        long t = 0;
        for (var b = 0; b < 10; b++, t += Frame)
        {
            rx.Process(outBuf, new[] { new RadioReceiverInput { Samples = Sine(1000, 0.3, t, Frame), RelativePower = power } });
            foreach (var s in outBuf)
                peak = Math.Max(peak, Math.Abs(s));
        }

        Assert.IsTrue(peak < 0.5f, $"peak={peak:F2}");
    }

    [TestMethod]
    public void Cvsd_CleanLinkIsIntelligibleAndBitErrorsGarble()
    {
        double Measure(double ber)
        {
            var codec = new CvsdVoiceCodec(Rate) { BitErrorRate = ber };
            var x = Sine(1000, 0.3, 0, Rate);
            codec.Process(x);
            return SinadDb(x[(Rate / 4)..], 1000);
        }

        var clean = Measure(0);
        var garbled = Measure(0.05);
        Assert.IsTrue(clean >= 10, $"clean={clean:F1} dB");
        Assert.IsTrue(garbled <= clean - 5, $"clean={clean:F1} garbled={garbled:F1} dB");
    }

    [TestMethod]
    public void DigitalVoice_BitErrorsRiseTowardsTheEdgeOfRange()
    {
        TransmissionSegment Seg(double receivingPower, Modulation modulation = Modulation.AM) =>
            new(new DeJitteredTransmission
            {
                PCMMonoAudio = new float[8], PCMAudioLength = 8, Modulation = modulation, Encryption = 1,
                Decryptable = true, ReceivingPower = receivingPower, Guid = "A", OriginalClientGuid = "A",
            });

        Assert.IsTrue(OpenFreqReceiverEffectsPipeline.EstimateBitErrorRate(Seg(0.0)) < 1e-6);
        Assert.IsTrue(OpenFreqReceiverEffectsPipeline.EstimateBitErrorRate(Seg(0.98)) > 0.01);
        Assert.AreEqual(0, OpenFreqReceiverEffectsPipeline.EstimateBitErrorRate(Seg(0.98, Modulation.MIDS)));
    }

    [TestMethod]
    public void AllRadioModes_PlaySpeechAtTheSameLevel()
    {
        // 3.5.1.15.4.5.7: other modes within 2 dB of the normal mode at the same volume setting.
        // Intercom is matched on recorded speech instead: this harmonic test voice is mostly a 140 Hz
        // fundamental, which the radio passband removes and the wider intercom keeps.
        double Level(Modulation modulation, short encryption)
        {
            var pipeline = new OpenFreqReceiverEffectsPipeline(1, Rate);
            var transmitter = new TransmitterProcessor(Rate);
            double sum = 0;
            long count = 0;
            long t = 0;
            for (var b = 0; b < 150; b++)
            {
                var voice = new float[Frame];
                for (var i = 0; i < Frame; i++, t++)
                {
                    var syllable = 0.5f + 0.5f * MathF.Sin(2 * MathF.PI * 4f * t / Rate);
                    float s = 0;
                    for (var h = 1; h <= 20; h++)
                        s += MathF.Sin(2 * MathF.PI * 140f * h * t / Rate + h) / h;
                    voice[i] = 0.1f * syllable * s;
                }

                transmitter.ApplyLevelControl(voice);
                TransmitterProcessor.ApplySpeechClipper(voice, modulation, encryption > 0);
                var output = new float[Frame];
                pipeline.ProcessSegments(output, new[]
                {
                    new TransmissionSegment(new DeJitteredTransmission
                    {
                        PCMMonoAudio = voice, PCMAudioLength = Frame, Modulation = modulation, Encryption = encryption,
                        Decryptable = true, Frequency = 251_000_000, ReceivingPower = 0.1, Guid = "A",
                        OriginalClientGuid = "A",
                    }),
                });
                if (b < 25)
                    continue;
                foreach (var s in output)
                    sum += s * s;
                count += Frame;
            }

            return 10 * Math.Log10(sum / count);
        }

        var am = Level(Modulation.AM, 0);
        foreach (var (modulation, encryption) in new[]
                 {
                     (Modulation.FM, (short)0), (Modulation.AM, (short)1), (Modulation.MIDS, (short)0),
                 })
        {
            var level = Level(modulation, encryption);
            Assert.IsTrue(Math.Abs(level - am) <= 2,
                $"{modulation}{(encryption > 0 ? "+ENC" : "")}={level:F1} dB vs AM={am:F1} dB");
        }
    }
}
