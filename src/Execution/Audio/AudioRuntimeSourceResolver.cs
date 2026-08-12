using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution.Graph;
using VideoStages.Planning;

namespace VideoStages.Execution.Audio;

internal sealed class AudioRuntimeSourceResolver(
    WorkflowGenerator g,
    AudioHandler audioHandler)
{
    public AudioRuntimeSources Resolve(VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Dictionary<int, WGNodeData> clipAudios = ResolveIndexedSources(plan);
        Dictionary<int, WGNodeData> uploadedAudios = ResolveUploadedSources(plan);
        audioHandler.PruneAceStepFunUnsavedTracks(plan.Clips);
        WGNodeData nativeAudio = plan.Root.IgnoresHostRootOutput
            ? null
            : g.CurrentMedia?.AttachedAudio;
        return new(nativeAudio, clipAudios, uploadedAudios);
    }

    private Dictionary<int, WGNodeData> ResolveIndexedSources(VideoExecutionPlan plan)
    {
        Dictionary<int, WGNodeData> sources = [];
        ControlNetCoreMediaCapture controlNet = new(g);
        foreach (ClipPlan clip in plan.Clips)
        {
            switch (clip.Audio.Base.Kind)
            {
                case AudioSourceKind.AceStepFun:
                {
                    if (clip.Audio.Base.AceStepFunTrack is not int track)
                    {
                        RequestWarnings.Track(
                            g.UserInput,
                            $"VideoStages: clip {clip.ClipId} selects an AceStepFun audio source "
                            + "without a valid track; continuing without that source and using "
                            + "native audio instead.");
                        break;
                    }
                    WGNodeData audio = audioHandler.DetectAceStepFunAudio(track);
                    if (audio is null)
                    {
                        RequestWarnings.Track(
                            g.UserInput,
                            $"VideoStages: clip {clip.ClipId} selects AceStepFun audio{track}, "
                            + "but AceStepFun did not publish that track; continuing without that "
                            + "source and using native audio instead.");
                        break;
                    }
                    sources[clip.ClipId] = audio;
                    break;
                }
                case AudioSourceKind.ControlNet:
                {
                    int? sourceIndex = ResolveControlNetSourceIndex(
                        clip,
                        controlNet);
                    if (!sourceIndex.HasValue)
                    {
                        break;
                    }
                    if (!controlNet.TryGetCapturedAudio(sourceIndex.Value, out WGNodeData audio))
                    {
                        RequestWarnings.Track(
                            g.UserInput,
                            $"VideoStages: clip {clip.ClipId} selects ControlNet "
                            + $"{sourceIndex.Value + 1} audio, but captured video audio is unavailable; "
                            + "using native audio instead (not using silence).");
                        break;
                    }
                    sources[clip.ClipId] = audio;
                    break;
                }
            }
        }
        return sources;
    }

    private int? ResolveControlNetSourceIndex(
        ClipPlan clip,
        ControlNetCoreMediaCapture controlNet)
    {
        if (clip.ArchitecturePayload is IArchitectureControlNetSourcePlan
                { ControlNetSourceIndex: int plannedIndex })
        {
            return plannedIndex;
        }

        List<int> capturedIndices = [];
        foreach (int index in ControlNetCoreMediaCapture.Indices)
        {
            if (controlNet.TryGetCapturedAudio(index, out WGNodeData _))
            {
                capturedIndices.Add(index);
            }
        }
        if (capturedIndices.Count == 1)
        {
            return capturedIndices[0];
        }

        string unavailable = capturedIndices.Count == 0
            ? "but no active ControlNet source has captured audio"
            : "without a unique valid ControlNet 1-3 drive source";
        RequestWarnings.Track(
            g.UserInput,
            $"VideoStages: clip {clip.ClipId} selects ControlNet audio {unavailable}; "
            + "using native audio instead (not using silence).");
        return null;
    }

    private Dictionary<int, WGNodeData> ResolveUploadedSources(VideoExecutionPlan plan)
    {
        Dictionary<int, WGNodeData> sources = [];
        foreach (ClipPlan clip in plan.Clips)
        {
            if (clip.Audio.Base.Kind != AudioSourceKind.Upload)
            {
                continue;
            }
            if (!clip.Audio.Base.HasConfiguredTrack)
            {
                RequestWarnings.Track(
                    g.UserInput,
                    $"VideoStages: clip {clip.ClipId} selects uploaded audio, but no uploaded "
                    + "file is attached; using native audio instead.");
                continue;
            }
            AudioFile uploaded = UploadedMedia.GetAudio(
                g.UserInput,
                clip.Audio.Base.UploadedMedia);
            if (uploaded is null)
            {
                continue;
            }
            string loadNodeId = g.CreateAudioLoadNode(uploaded, "${vsaudioupload}");
            WGNodeData source = new(
                new JArray(loadNodeId, 0),
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
            int frames = clip.Frames.GetValueOrDefault();
            double clipDuration =
                clip.Audio.LengthOwner != AudioLengthOwner.Audio
                && frames > 0
                && plan.FramesPerSecond > 0
                    ? frames / (double)plan.FramesPerSecond
                    : 0;
            if (clip.Audio.Base.LengthSeconds > 0 || clipDuration > 0)
            {
                using WorkflowBridge bridge = BridgeSync.For(g);
                INodeOutput current = bridge.ResolvePath(source.Path);
                if (clip.Audio.Base.LengthSeconds > 0)
                {
                    TrimAudioDurationNode trim = bridge.AddNode(
                        new TrimAudioDurationNode().With(
                        StartIndex: clip.Audio.Base.TrimStartSeconds,
                        Duration: clip.Audio.Base.LengthSeconds));
                    trim.Audio.ConnectToUntyped(current);
                    current = trim.AUDIO;
                }
                if (clipDuration > 0)
                {
                    EmptyAudioNode silence = bridge.AddNode(
                        new EmptyAudioNode().With(Duration: clipDuration));
                    AudioConcatNode padded = bridge.AddNode(
                        new AudioConcatNode().With(Direction: "after"));
                    padded.Audio1.ConnectToUntyped(current);
                    padded.Audio2.ConnectTo(silence.AUDIO);
                    TrimAudioDurationNode conform = bridge.AddNode(
                        new TrimAudioDurationNode().With(
                            StartIndex: 0,
                            Duration: clipDuration));
                    conform.Audio.ConnectTo(padded.AUDIO);
                    current = conform.AUDIO;
                }
                source = new(
                    WorkflowBridge.ToPath(current),
                    g,
                    WGNodeData.DT_AUDIO,
                    source.Compat);
            }
            sources[clip.ClipId] = source;
        }
        return sources;
    }
}
