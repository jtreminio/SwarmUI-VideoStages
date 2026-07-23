using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>Materializes every compiled clip audio identity exactly once.</summary>
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
        WGNodeData nativeAudio = plan.Root.NativeAudioDisposition
            == NativeAudioDisposition.DiscardWithRoot
            ? null
            : g.CurrentMedia?.AttachedAudio;
        return new(nativeAudio, clipAudios, uploadedAudios);
    }

    private Dictionary<int, WGNodeData> ResolveIndexedSources(VideoExecutionPlan plan)
    {
        Dictionary<int, WGNodeData> sources = [];
        ControlNetAudioCapture controlNet = new(g);
        foreach (ClipPlan clip in plan.Clips)
        {
            switch (clip.Audio.Base.Kind)
            {
                case AudioBaseSourceKind.AceStepFun:
                {
                    int track = clip.Audio.Base.AceStepFunTrack
                        ?? throw new SwarmUserErrorException(
                            $"VideoStages: clip {clip.ClipId} selects an AceStepFun audio source "
                            + "without a valid track.");
                    sources[clip.ClipId] = audioHandler.DetectAceStepFunAudio(track)
                        ?? throw new SwarmUserErrorException(
                            $"VideoStages: clip {clip.ClipId} selects AceStepFun audio{track}, "
                            + "but that track is not present in the workflow.");
                    break;
                }
                case AudioBaseSourceKind.ControlNet:
                {
                    int sourceIndex = ResolveControlNetSourceIndex(
                        clip,
                        controlNet);
                    if (!controlNet.TryGetCapturedAudio(sourceIndex, out WGNodeData audio))
                    {
                        throw new SwarmUserErrorException(
                            $"VideoStages: clip {clip.ClipId} selects ControlNet "
                            + $"{sourceIndex + 1} audio, but captured video audio is unavailable.");
                    }
                    sources[clip.ClipId] = audio;
                    break;
                }
            }
        }
        return sources;
    }

    private static int ResolveControlNetSourceIndex(
        ClipPlan clip,
        ControlNetAudioCapture controlNet)
    {
        if (clip.ArchitecturePayload is IArchitectureControlNetSourcePlan
                { ControlNetSourceIndex: int plannedIndex })
        {
            return plannedIndex;
        }

        List<int> capturedIndices = [];
        for (int index = 0; index <= 2; index++)
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

        throw new SwarmUserErrorException(
            $"VideoStages: clip {clip.ClipId} selects ControlNet audio without a "
            + "unique valid ControlNet 1-3 drive source.");
    }

    private Dictionary<int, WGNodeData> ResolveUploadedSources(VideoExecutionPlan plan)
    {
        Dictionary<int, WGNodeData> sources = [];
        foreach (ClipPlan clip in plan.Clips)
        {
            if (clip.Audio.Base.Kind != AudioBaseSourceKind.Upload)
            {
                continue;
            }
            AudioFile uploaded = EmbeddedMediaMaterializer.MaterializeAudio(
                g,
                clip.Audio.Base.UploadedMedia);
            if (uploaded is null)
            {
                continue;
            }
            string loadNodeId = g.CreateAudioLoadNode(uploaded, "${vsaudioupload}");
            sources[clip.ClipId] = new WGNodeData(
                new JArray(loadNodeId, 0),
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
        }
        return sources;
    }
}
