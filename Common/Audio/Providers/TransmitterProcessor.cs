using System;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using OpenFreqAudio;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;

/// <summary>
/// The sender's side of the radio, one instance per sender per receiving radio: automatic level control
/// (MIL-R-29583 3.5.1.16.8.2) and, for analog radio, the speech clipper that drives ~85% modulation.
/// Cockpit ambience is added between the two, after the voice is levelled, so it sits at a fixed distance
/// below the voice instead of being pumped up whenever the talker pauses.
/// </summary>
public sealed class TransmitterProcessor
{
    internal const float AlcTarget = 0.95f; // peak-follower target; a steady tone lands at ~85% AM
    private const float AlcMaxGain = 10f; // +20 dB, so pauses don't pump mic noise up
    private const float ClipperKnee = 0.6f;

    private readonly AttackDecayFilter _alcEnvelope;

    public TransmitterProcessor(int sampleRate)
    {
        // Fast attack catches the first syllable; slow release holds gain through words.
        _alcEnvelope = AttackDecayFilter.MakeAttackDecayFilter(0.02, 1.0, sampleRate);
        _alcEnvelope.D1 = AlcTarget / AlcMaxGain; // start at full gain so quiet senders are heard at once
    }

    public void ApplyLevelControl(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var level = _alcEnvelope.Apply(MathF.Abs(samples[i]));
            samples[i] *= Math.Min(AlcMaxGain, AlcTarget / Math.Max(level, 1e-6f));
        }
    }

    /// <summary>
    /// Analog radio speech clipper. Intercom has no RF stage, and digital voice (KY-58, MIDS) is encoded
    /// straight from the levelled microphone, so both skip it.
    /// </summary>
    public static void ApplySpeechClipper(Span<float> samples, Modulation modulation, bool encrypted)
    {
        if (OpenFreqReceiverEffectsPipeline.KindOf(modulation, encrypted) is not
            (OpenFreqReceiverEffectsPipeline.ReceiverKind.Am or OpenFreqReceiverEffectsPipeline.ReceiverKind.Fm))
            return;

        for (var i = 0; i < samples.Length; i++)
            samples[i] = SoftClip(samples[i]);
    }

    /// <summary>Linear below the knee, tanh above it; never exceeds ±1 (100% modulation).</summary>
    internal static float SoftClip(float x)
    {
        var a = MathF.Abs(x);
        if (a <= ClipperKnee)
            return x;
        var range = 1f - ClipperKnee;
        return MathF.CopySign(ClipperKnee + range * MathF.Tanh((a - ClipperKnee) / range), x);
    }
}
