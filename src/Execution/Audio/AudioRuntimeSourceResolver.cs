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
        WGNodeData nativeAudio = plan.Root.DiscardsRoot
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
                            + "without a valid track; continuing without that source.");
                        break;
                    }
                    WGNodeData audio = audioHandler.DetectAceStepFunAudio(track);
                    if (audio is null)
                    {
                        RequestWarnings.Track(
                            g.UserInput,
                            $"VideoStages: clip {clip.ClipId} selects AceStepFun audio{track}, "
                            + "but that track is not present in the workflow; continuing without that source.");
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
                            + "using silence instead.");
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

        RequestWarnings.Track(
            g.UserInput,
            $"VideoStages: clip {clip.ClipId} selects ControlNet audio without a "
            + "unique valid ControlNet 1-3 drive source; using silence instead.");
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
            AudioFile uploaded = UploadedMedia.GetAudio(
                g.UserInput,
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
