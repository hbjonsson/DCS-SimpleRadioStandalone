using System;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Recording;

/// <summary>
/// Forwards finalized per-radio receive PCM to the recording sink.
/// Keeping this boundary free of transmission metadata makes the 455th
/// application-wide recording policy explicit.
/// </summary>
internal sealed class ProcessedAudioRecordingTap
{
    private readonly int _radioId;
    private readonly IAudioRecordingSink _sink;

    public ProcessedAudioRecordingTap(int radioId, IAudioRecordingSink sink)
    {
        _radioId = radioId;
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public void Append(ReadOnlySpan<float> processedAudio)
    {
        _sink.AppendProcessedClientAudio(_radioId, processedAudio);
    }
}
