using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>Resolves clip audio and assembles a concat aligned to video boundary overlaps.</summary>
internal static class MultiClipAudioGraphAssembler
{
    private const long SilenceSampleRate = 44100;
    private const long SilenceChannels = 2;

    public static WGNodeData TryGetConcatenatableAudio(WGNodeData clip, WGNodeData audioVae)
    {
        WGNodeData attached = clip?.AttachedAudio;
        if (attached?.Path is not JArray { Count: 2 })
        {
            return null;
        }
        if (attached.DataType == WGNodeData.DT_AUDIO)
        {
            return attached;
        }
        if (attached.DataType == WGNodeData.DT_LATENT_AUDIO && audioVae is not null)
        {
            WGNodeData decoded = attached.DecodeLatents(audioVae, true);
            if (decoded?.Path is JArray { Count: 2 } && decoded.DataType == WGNodeData.DT_AUDIO)
            {
                return decoded;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves one decoded audio input per clip. When at least one clip has audio, clips without
    /// audio receive a duration-matched silent track so one silent clip cannot erase the rest of
    /// the timeline's audio.
    /// </summary>
    public static IReadOnlyList<INodeOutput> ResolveOrPadTimelineAudio(
        WorkflowBridge bridge,
        IReadOnlyList<WGNodeData> clips,
        WGNodeData audioVae)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(clips);
        WGNodeData[] resolved = [.. clips.Select(clip => TryGetConcatenatableAudio(clip, audioVae))];
        if (!clips.Any(clip => clip?.AttachedAudio?.Path is JArray { Count: 2 }))
        {
            return [];
        }

        List<INodeOutput> outputs = [];
        for (int i = 0; i < clips.Count; i++)
        {
            WGNodeData clip = clips[i];
            WGNodeData audio = resolved[i];
            if (audio is not null)
            {
                INodeOutput output = bridge.ResolvePath(audio.Path);
                if (output is null)
                {
                    throw new SwarmUserErrorException(
                        $"VideoStages: clip {i} audio could not be resolved for timeline assembly.");
                }
                outputs.Add(output);
                continue;
            }

            if (clip?.AttachedAudio?.Path is JArray { Count: 2 })
            {
                throw new SwarmUserErrorException(
                    $"VideoStages: clip {i} has attached audio that cannot be decoded for timeline assembly.");
            }
            if (clip?.Frames is not > 0 || clip.GetRawFPS() is not > 0)
            {
                throw new SwarmUserErrorException(
                    $"VideoStages: clip {i} has no audio and its duration is unavailable, "
                    + "so timeline silence cannot be created.");
            }

            EmptyAudioNode silence = bridge.AddNode(new EmptyAudioNode()).With(
                Duration: clip.Frames.Value / (double)clip.GetRawFPS().Value,
                SampleRate: SilenceSampleRate,
                Channels: SilenceChannels);
            bridge.SyncNode(silence);
            outputs.Add(silence.AUDIO);
        }
        return outputs;
    }

    /// <summary>
    /// Concatenates clip audio after dropping each outgoing overlap from its source track. This keeps
    /// later audio aligned with its earlier-in-the-timeline video after every interior boundary.
    /// </summary>
    public static INodeOutput Merge(
        WorkflowBridge bridge,
        IReadOnlyList<WGNodeData> clips,
        IReadOnlyList<INodeOutput> audioOutputs,
        BoundaryOverlapPlan plan)
    {
        if (plan is null)
        {
            return CascadeConcat(bridge, audioOutputs);
        }

        int fps = clips[0].GetRawFPS().Value;
        List<INodeOutput> aligned = [];
        for (int i = 0; i < audioOutputs.Count; i++)
        {
            int rightOverlap = i < audioOutputs.Count - 1 ? plan.BoundaryOverlap[i] : 0;
            aligned.Add(rightOverlap > 0
                ? TrimToDuration(bridge, audioOutputs[i], Math.Max(0, clips[i].Frames.Value - rightOverlap) / (double)fps)
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
            bridge.SyncNode(concat);
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
