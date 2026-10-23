using System;
using System.Collections.Generic;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Recording;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Utility;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Tests.Audio;

[TestClass]
public class PostDspRecordingTests
{
    [TestMethod]
    public void ProcessedTap_ForwardsExactSamplesAndRadio()
    {
        var sink = new CapturingRecordingSink();
        var tap = new ProcessedAudioRecordingTap(3, sink);
        var processed = new[] { -1f, -0.25f, 0f, 0.5f, 1f };

        tap.Append(processed);
        processed[0] = 0.75f;

        Assert.AreEqual(1, sink.Blocks.Count);
        Assert.AreEqual(3, sink.Blocks[0].RadioId);
        CollectionAssert.AreEqual(
            new[] { -1f, -0.25f, 0f, 0.5f, 1f },
            sink.Blocks[0].Samples);
    }

    [TestMethod]
    public void RadioMixer_CapturesOpenFreqPlaybackAndDrainExactlyOnce()
    {
        var sink = new CapturingRecordingSink();
        var mixer = CreateMixer(sink);
        var frameLength = Constants.OUTPUT_SEGMENT_FRAMES;
        var voice = new float[frameLength];
        for (var i = 0; i < voice.Length; i++)
            voice[i] = 0.2f * MathF.Sin(2f * MathF.PI * 800f * i / Constants.OUTPUT_SAMPLE_RATE);

        var segment = MakeSegment(voice);
        for (var i = 0; i < 4; i++)
        {
            mixer.QueueIncomingAudio(
                new[] { segment },
                frameLength,
                out var openFreqOnly,
                out _,
                out var hasFallback);
            Assert.IsTrue(openFreqOnly);
            Assert.IsFalse(hasFallback);

            var playback = ReadQueuedBlock(mixer, frameLength);
            CollectionAssert.AreEqual(playback, sink.Blocks[^1].Samples);
        }

        var blocksBeforeDrain = sink.Blocks.Count;
        Assert.IsTrue(mixer.QueueOpenFreqDrain(frameLength));
        var drainPlayback = ReadQueuedBlock(mixer, frameLength);

        Assert.AreEqual(blocksBeforeDrain + 1, sink.Blocks.Count);
        CollectionAssert.AreEqual(drainPlayback, sink.Blocks[^1].Samples);
        Assert.IsTrue(Array.Exists(sink.Blocks[^2].Samples, sample => MathF.Abs(sample) > 0.001f));
        Assert.IsTrue(Array.TrueForAll(drainPlayback, float.IsFinite));
    }

    [TestMethod]
    public void RadioMixer_CapturesFmReceiverPlaybackBlock()
    {
        var sink = new CapturingRecordingSink();
        var mixer = CreateMixer(sink);
        var audio = new float[Constants.OUTPUT_SEGMENT_FRAMES];
        for (var i = 0; i < audio.Length; i++)
            audio[i] = 0.15f * MathF.Sin(2f * MathF.PI * 600f * i / Constants.OUTPUT_SAMPLE_RATE);

        mixer.QueueIncomingAudio(
            new[] { MakeSegment(audio, Modulation.FM) },
            audio.Length,
            out var openFreqOnly,
            out _,
            out var hasFallback);

        var playback = ReadQueuedBlock(mixer, audio.Length);
        Assert.IsTrue(openFreqOnly);
        Assert.IsFalse(hasFallback);
        Assert.AreEqual(1, sink.Blocks.Count);
        CollectionAssert.AreEqual(playback, sink.Blocks[0].Samples);
    }

    [TestMethod]
    public void RadioMixer_MixedBucketsRecordOneFinalClippedBlock()
    {
        var sink = new CapturingRecordingSink();
        var mixer = CreateMixer(sink);
        var length = Constants.OUTPUT_SEGMENT_FRAMES;
        var am = new float[length];
        var fm = new float[length];
        Array.Fill(am, 0.8f);
        Array.Fill(fm, 0.8f);

        mixer.QueueIncomingAudio(
            new[]
            {
                MakeSegment(am, Modulation.AM),
                MakeSegment(fm, Modulation.AM, decryptable: false),
            },
            length,
            out var openFreqOnly,
            out _,
            out var hasFallback);

        var playback = ReadQueuedBlock(mixer, length);
        Assert.IsFalse(openFreqOnly);
        Assert.IsTrue(hasFallback);
        Assert.AreEqual(1, sink.Blocks.Count);
        CollectionAssert.AreEqual(playback, sink.Blocks[0].Samples);
        Assert.IsTrue(Array.TrueForAll(playback, sample => sample is >= -1f and <= 1f));
    }

    [TestMethod]
    public void CircularBuffer_SpanWriteCopiesAndWraps()
    {
        var buffer = new CircularFloatBuffer(5);
        buffer.Write(new[] { 1f, 2f, 3f }, 0, 3);
        var firstRead = new float[2];
        buffer.Read(firstRead, 0, firstRead.Length);

        buffer.Write(new ReadOnlySpan<float>(new[] { 4f, 5f, 6f, 7f }));

        var remaining = new float[5];
        Assert.AreEqual(5, buffer.Read(remaining, 0, remaining.Length));
        CollectionAssert.AreEqual(new[] { 3f, 4f, 5f, 6f, 7f }, remaining);
    }

    private static RadioMixingProvider CreateMixer(IAudioRecordingSink sink)
    {
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(Constants.OUTPUT_SAMPLE_RATE, 2);
        return new RadioMixingProvider(waveFormat, radioId: 1, audioRecordingSink: sink);
    }

    private static float[] ReadQueuedBlock(RadioMixingProvider mixer, int length)
    {
        var playback = new float[length];
        Assert.AreEqual(length, mixer.ReadQueuedAudio(playback));
        return playback;
    }

    private static TransmissionSegment MakeSegment(
        float[] audio,
        Modulation modulation = Modulation.AM,
        bool decryptable = true)
    {
        return new TransmissionSegment(new DeJitteredTransmission
        {
            PCMMonoAudio = audio,
            PCMAudioLength = audio.Length,
            Modulation = modulation,
            Decryptable = decryptable,
            Frequency = 125_000_000,
            ReceivingPower = 0.1,
            Volume = 1f,
            Guid = "AAAAAAAAAAAAAAAAAAAAAA",
            OriginalClientGuid = "AAAAAAAAAAAAAAAAAAAAAA",
        });
    }

    private sealed class CapturingRecordingSink : IAudioRecordingSink
    {
        public List<(int RadioId, float[] Samples)> Blocks { get; } = new();

        public void AppendProcessedClientAudio(int radioId, ReadOnlySpan<float> processedPcm)
        {
            Blocks.Add((radioId, processedPcm.ToArray()));
        }
    }
}
