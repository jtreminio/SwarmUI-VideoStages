using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.LTX2;

namespace VideoStages;

internal sealed class VideoStagesCoordinator(
    WorkflowGenerator g,
    RootVideoStageHandoff rootVideoStageHandoff,
    StageSequenceRunner stageSequenceRunner,
    AudioHandler audioHandler,
    LtxManager ltxManager)
{
    private const int FinalStageSaveId = 52200;

    public void RunConfiguredStages()
    {
        VideoStagesSpec spec = g.GetVideoStagesSpec();
        if (!VideoStagesSpecParser.HasUsableVideoModel(g, spec))
        {
            return;
        }

        List<ClipSpec> clips = [.. spec.Clips];
        bool refineSourceVideo = TryInstallRefineSourceVideo(clips);
        bool rootStageHandoff = !refineSourceVideo && rootVideoStageHandoff.ShouldHandoffRootStage();
        if (clips.Count == 0)
        {
            TryInjectConfiguredAudio(clips);
            return;
        }
        EnsureComfyDependencies(clips);

        ClipAudioMaps clipAudioMaps = BuildClipAudioMaps(clips);
        if (!rootStageHandoff)
        {
            ClipSpec firstClip = clips[0];
            StageSpec first = firstClip.Stages[0];
            TryApplyControlNetClipLength(
                firstClip.ClipLengthFromControlNet,
                firstClip.ControlNetSource,
                first.Model);
            TryInjectResolvedClipAudio(
                firstClip.Id,
                firstClip.AudioSource,
                firstClip.ClipLengthFromAudio,
                clipAudioMaps);
        }

        g.LastID = Math.Max(g.LastID, Constants.StagedNodeIdReservationFloor);

        stageSequenceRunner.Run(
            clips,
            clipAudioMaps.NativeAudio,
            clipAudioMaps.ClipAudios,
            clipAudioMaps.UploadedAudios,
            rootStageHandoff);
        EnsureFinalStageOutputSaved();
    }

    private bool TryInstallRefineSourceVideo(IReadOnlyList<ClipSpec> clips)
    {
        if (!g.UserInput.TryGet(VideoStagesExtension.RefineSourceVideo, out Image refineSource)
            || refineSource is null
            || clips.Count == 0)
        {
            return false;
        }
        if (refineSource.Type?.MetaType != MediaMetaType.Video)
        {
            Logs.Warning(
                "VideoStages: 'Refine Source Video' was set but its media type is not video. "
                + "Ignoring and falling back to the normal pipeline.");
            return false;
        }

        WGNodeData loadedVideo = g.LoadImage(refineSource, "${vsrefinesource}", resize: false);
        g.CurrentMedia = loadedVideo;
        return true;
    }

    private void EnsureComfyDependencies(IReadOnlyList<ClipSpec> clips)
    {
        if (g.Features.Contains(Constants.LtxVideoFeatureFlag)
            || !clips.Any(clip =>
                !string.IsNullOrWhiteSpace(clip.ControlNetLora)
                && clip.Stages.Any(stage => VideoStageModelCompat.IsLtxV2VideoModel(stage.Model))))
        {
            return;
        }

        throw new SwarmUserErrorException(
            "VideoStages ControlNet LoRA requires the ComfyUI-LTXVideo custom nodes. "
            + $"Install {Constants.LtxVideoNodeUrl} or use SwarmUI's LTXVideo feature installer.");
    }

    private void TryInjectConfiguredAudio(List<ClipSpec> clips)
    {
        if (clips.Count == 0)
        {
            ltxManager.TryInjectAudio(g.CurrentMedia?.AttachedAudio);
            return;
        }

        ClipAudioMaps clipAudioMaps = BuildClipAudioMaps(clips);
        ClipSpec first = clips[0];
        string firstClipStageModel = first.Stages is { Count: > 0 }
            ? first.Stages[0].Model
            : null;
        TryApplyControlNetClipLength(
            first.ClipLengthFromControlNet,
            first.ControlNetSource,
            firstClipStageModel);
        TryInjectResolvedClipAudio(
            first.Id,
            first.AudioSource,
            first.ClipLengthFromAudio,
            clipAudioMaps);
    }

    private void TryApplyControlNetClipLength(
        bool clipLengthFromControlNet,
        string controlNetSource,
        string stageVideoModelName)
    {
        if (clipLengthFromControlNet && VideoStageModelCompat.IsLtxV2VideoModel(stageVideoModelName))
        {
            _ = ltxManager.TryApplyControlNetFrameCount(controlNetSource);
        }
    }

    private readonly record struct ClipAudioMaps(
        WGNodeData NativeAudio,
        IReadOnlyDictionary<int, WGNodeData> ClipAudios,
        IReadOnlyDictionary<int, WGNodeData> UploadedAudios);

    private ClipAudioMaps BuildClipAudioMaps(IReadOnlyList<ClipSpec> clips)
    {
        WGNodeData nativeAudio = g.CurrentMedia?.AttachedAudio;
        IReadOnlyDictionary<int, WGNodeData> clipAudios =
            BuildPerClipExternalAudioDetections(clips);
        IReadOnlyDictionary<int, WGNodeData> uploadedAudios =
            BuildPerClipUploadDetections(clips);
        audioHandler.PruneAceStepFunUnsavedTracks(clips);
        return new ClipAudioMaps(nativeAudio, clipAudios, uploadedAudios);
    }

    private IReadOnlyDictionary<int, WGNodeData> BuildPerClipExternalAudioDetections(
        IReadOnlyList<ClipSpec> clips)
    {
        Dictionary<int, WGNodeData> audios = new(BuildPerClipAudioDetections(audioHandler, clips));
        ControlNetApplicator applicator = new(g);
        foreach (ClipSpec clip in clips)
        {
            if (!ClipAudioWorkflowHelper.IsControlNetAudioSource(clip.AudioSource))
            {
                continue;
            }
            if (applicator.TryGetCapturedControlNetAudio(clip.ControlNetSource, out WGNodeData audio))
            {
                audios[clip.Id] = audio;
            }
        }
        return audios;
    }

    private void TryInjectResolvedClipAudio(
        int clipId,
        string audioSource,
        bool clipLengthFromAudio,
        ClipAudioMaps maps)
    {
        WGNodeData audio = ClipAudioWorkflowHelper.ResolveClipAudio(
            clipId,
            audioSource,
            maps.NativeAudio,
            maps.ClipAudios,
            maps.UploadedAudios,
            suppressNativeFallback: false,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.CoordinatorField);
        _ = ltxManager.TryInjectAudio(
            audio,
            ClipAudioWorkflowHelper.ShouldMatchVideoLengthForTryInjectAudio(
                audioSource,
                clipLengthFromAudio,
                restrictLengthMatchToUploadOrAce: false));
    }

    private static IReadOnlyDictionary<int, WGNodeData> BuildPerClipAudioDetections(
        AudioHandler handler,
        IReadOnlyList<ClipSpec> clips)
    {
        Dictionary<int, WGNodeData> audios = [];
        foreach (ClipSpec clip in clips)
        {
            WGNodeData audio = handler.DetectAceStepFunAudio(clip.AudioSource);
            if (audio is not null)
            {
                audios[clip.Id] = audio;
            }
        }
        return audios;
    }

    private IReadOnlyDictionary<int, WGNodeData> BuildPerClipUploadDetections(
        IReadOnlyList<ClipSpec> clips)
    {
        Dictionary<int, WGNodeData> audios = [];
        foreach (ClipSpec clip in clips)
        {
            if (!StringUtils.Equals(clip.AudioSource, Constants.AudioSourceUpload))
            {
                continue;
            }
            AudioFile uploaded = VideoStagesSpecParser.MaterializeUploadedAudioForClip(g, clip);
            if (uploaded is null)
            {
                continue;
            }
            string loadNodeId = g.CreateAudioLoadNode(uploaded, "${vsaudioupload}");
            audios[clip.Id] = new WGNodeData(
                new JArray(loadNodeId, 0),
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
        }
        return audios;
    }

    private void EnsureFinalStageOutputSaved()
    {
        if (g.UserInput.Get(T2IParamTypes.DoNotSave, false) || g.CurrentMedia is null)
        {
            return;
        }

        string saveId = g.GetStableDynamicID(FinalStageSaveId, 0);
        if (g.CurrentMedia.Path is JArray { Count: 2 } currentPath)
        {
            WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            INodeOutput output = bridge.ResolvePath(currentPath);
            if (output is not null
                && bridge.Graph.FindNearestDownstream<SwarmSaveAnimationWSNode>(output) is not null)
            {
                return;
            }
        }

        g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, saveId);
    }
}
