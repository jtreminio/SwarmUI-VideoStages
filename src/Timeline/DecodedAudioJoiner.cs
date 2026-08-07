using ComfyTyped.Core;
using ComfyTyped.Generated;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Timeline;

/// <summary>Resolves clip audio and joins it on the same boundary timeline as the video.</summary>
internal static class DecodedAudioJoiner
{
    /// <summary>
    /// Returns one audio input per clip, or nothing when no clip has audio. Clips without audio
    /// receive duration-matched silence so one silent clip cannot erase the timeline's audio.
    /// </summary>
    internal static IReadOnlyList<INodeOutput> MaterializeTimelineAudio(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(clips);
        if (!clips.Any(clip => clip?.Audio is not null))
        {
            return [];
        }

        List<INodeOutput> outputs = new(clips.Count);
        foreach (DecodedClipArtifact clip in clips)
        {
            if (clip.Audio is null)
            {
                EmptyAudioNode silence = bridge.AddNode(new EmptyAudioNode()).With(
                    Duration: clip.Frames / (double)clip.FramesPerSecond,
                    SampleRate: 44100,
                    Channels: 2);
                outputs.Add(silence.AUDIO);
                continue;
            }
            outputs.Add(clip.Audio.Resolve(bridge)
                ?? throw Invariant.Failure(
                    $"clip {clip.ClipId} decoded audio could not be resolved "
                    + "for the timeline merge."));
        }
        return outputs;
    }

    /// <summary>
    /// Drops a Continue pre-roll that the video side already discarded because its boundary
    /// degraded to a cut at runtime.
    /// </summary>
    internal static IReadOnlyList<INodeOutput> TrimDiscardedHandles(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips,
        IReadOnlyList<INodeOutput> audioOutputs,
        IReadOnlyList<int> discardedHandles)
    {
        if (audioOutputs.Count == 0 || !discardedHandles.Any(handle => handle > 0))
        {
            return audioOutputs;
        }

        List<INodeOutput> trimmed = [.. audioOutputs];
        for (int i = 0; i < discardedHandles.Count; i++)
        {
            if (discardedHandles[i] > 0)
            {
                trimmed[i] = TrimToRange(
                    bridge,
                    trimmed[i],
                    discardedHandles[i] / (double)clips[i].FramesPerSecond,
                    clips[i].Frames / (double)clips[i].FramesPerSecond);
            }
        }
        return trimmed;
    }

    /// <summary>
    /// Concatenates clip audio on the resolved video-overlap timeline. Continue pre-roll is a hidden
    /// incoming handle; crossfades trim the outgoing side.
    /// </summary>
    public static INodeOutput Merge(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips,
        IReadOnlyList<INodeOutput> audioOutputs,
        TimelineOverlapTrims plan)
    {
        if (plan is null)
        {
            return CascadeConcat(bridge, audioOutputs);
        }
        int fps = clips[0].FramesPerSecond;
        List<INodeOutput> aligned = [];
        for (int i = 0; i < audioOutputs.Count; i++)
        {
            int leftHandle = i > 0 ? plan.IncomingHandleFrames[i - 1] : 0;
            int rightReduction = i < audioOutputs.Count - 1
                ? Math.Max(0, plan.TrimFrames[i] - plan.IncomingHandleFrames[i])
                : 0;
            aligned.Add(leftHandle > 0 || rightReduction > 0
                ? TrimToRange(
                    bridge,
                    audioOutputs[i],
                    leftHandle / (double)fps,
                    Math.Max(0, clips[i].Frames - leftHandle - rightReduction) / (double)fps)
                : audioOutputs[i]);
        }
        return CascadeConcat(bridge, aligned);
    }

    private static INodeOutput CascadeConcat(WorkflowBridge bridge, IReadOnlyList<INodeOutput> audioOutputs)
    {
        INodeOutput acc = audioOutputs[0];
        for (int i = 1; i < audioOutputs.Count; i++)
        {
            AudioConcatNode concat = bridge.AddNode(new AudioConcatNode());
            concat.Audio1.ConnectToUntyped(acc);
            concat.Audio2.ConnectToUntyped(audioOutputs[i]);
            acc = concat.AUDIO;
        }
        return acc;
    }

    private static INodeOutput TrimToRange(
        WorkflowBridge bridge,
        INodeOutput audio,
        double startSeconds,
        double durationSeconds)
    {
        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode()).With(
            StartIndex: startSeconds,
            Duration: durationSeconds);
        trim.Audio.ConnectToUntyped(audio);
        return trim.AUDIO;
    }
}
