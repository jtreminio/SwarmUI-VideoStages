using ComfyTyped.Core;
using ComfyTyped.Generated;
using VideoStages.Execution;

namespace VideoStages;

/// <summary>Resolves clip audio and assembles a concat aligned to video boundary overlaps.</summary>
internal static class MultiClipAudioGraphAssembler
{
    private const long SilenceSampleRate = 44100;
    private const long SilenceChannels = 2;

    internal sealed record TimelineAudioPreflight(
        IReadOnlyList<INodeOutput> DecodedOutputs,
        bool HasAudio);

    /// <summary>
    /// Resolves every architecture-neutral decoded audio handle without mutating the graph.
    /// Missing entries remain null and are materialized as silence only after the whole timeline
    /// has passed preflight.
    /// </summary>
    internal static TimelineAudioPreflight PreflightTimelineAudio(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(clips);
        if (!clips.Any(clip => clip?.Audio is not null))
        {
            return new([], HasAudio: false);
        }

        List<INodeOutput> outputs = new(clips.Count);
        for (int i = 0; i < clips.Count; i++)
        {
            DecodedClipArtifact clip = clips[i];
            if (clip.Audio is null)
            {
                outputs.Add(null);
                continue;
            }

            INodeOutput output = clip.Audio.Resolve(bridge);
            if (output is null)
            {
                throw VideoStagesInvariant.Failure(
                    $"VideoStages: clip {clip.ClipId} decoded audio could not be resolved "
                    + "for timeline assembly.");
            }
            outputs.Add(output);
        }
        return new(outputs, HasAudio: true);
    }

    /// <summary>
    /// Produces one audio input per clip from an already validated preflight. Clips without audio
    /// receive duration-matched silence so one silent clip cannot erase the timeline's audio.
    /// </summary>
    internal static IReadOnlyList<INodeOutput> MaterializeTimelineAudio(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips,
        TimelineAudioPreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(preflight);
        if (!preflight.HasAudio)
        {
            return [];
        }
        if (preflight.DecodedOutputs.Count != clips.Count)
        {
            throw VideoStagesInvariant.Failure(
                "Timeline audio preflight does not match the decoded clip count.");
        }

        List<INodeOutput> outputs = new(clips.Count);
        for (int i = 0; i < clips.Count; i++)
        {
            DecodedClipArtifact clip = clips[i];
            INodeOutput decodedAudio = preflight.DecodedOutputs[i];
            if (decodedAudio is not null)
            {
                outputs.Add(decodedAudio);
                continue;
            }

            EmptyAudioNode silence = bridge.AddNode(new EmptyAudioNode()).With(
                Duration: clip.Frames / (double)clip.FramesPerSecond,
                SampleRate: SilenceSampleRate,
                Channels: SilenceChannels);
            outputs.Add(silence.AUDIO);
        }
        return outputs;
    }

    /// <summary>
    /// Concatenates clip audio on the resolved video-overlap timeline. The incoming clip owns each
    /// overlap, including any audio continuation it generated from boundary context.
    /// </summary>
    public static INodeOutput Merge(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips,
        IReadOnlyList<INodeOutput> audioOutputs,
        BoundaryOverlapPlan plan)
    {
        if (plan is null)
        {
            return CascadeConcat(bridge, audioOutputs);
        }
        int fps = clips[0].FramesPerSecond;
        List<INodeOutput> aligned = [];
        for (int i = 0; i < audioOutputs.Count; i++)
        {
            int rightOverlap = i < audioOutputs.Count - 1 ? plan.BoundaryOverlap[i] : 0;
            aligned.Add(rightOverlap > 0
                ? TrimToDuration(
                    bridge,
                    audioOutputs[i],
                    Math.Max(0, clips[i].Frames - rightOverlap) / (double)fps)
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

    private static INodeOutput TrimToDuration(WorkflowBridge bridge, INodeOutput audio, double durationSeconds)
    {
        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode()).With(
            StartIndex: 0.0,
            Duration: durationSeconds);
        trim.Audio.ConnectToUntyped(audio);
        return trim.AUDIO;
    }
}
