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
        if (!g.TryGetVideoStagesSpec(out _))
        {
            return;
        }

        List<ClipSpec> clips = [.. g.GetVideoStagesSpec().Clips];
        List<ClipWithStages> clipsWithStages = g.GetClipsWithStages();
        bool rootStageHandoff = rootVideoStageHandoff.ShouldHandoffRootStage();
        if (clipsWithStages.Count == 0)
        {
            TryInjectConfiguredAudio(clips);
            return;
        }
        EnsureComfyDependencies(clipsWithStages);

        ClipAudioMaps clipAudioMaps = BuildClipAudioMaps(clips);
        if (!rootStageHandoff)
        {
            StageSpec first = clipsWithStages[0].Stages[0];
            TryApplyControlNetClipLength(
                first.ClipLengthFromControlNet,
                first.ClipControlNetSource,
                first.Model);
            TryInjectResolvedClipAudio(
                first.ClipId,
                first.ClipAudioSource,
                first.ClipLengthFromAudio && !first.ClipLengthFromControlNet,
                clipAudioMaps);
        }

        stageSequenceRunner.Run(
            clipsWithStages,
            clipAudioMaps.NativeAudio,
            clipAudioMaps.ClipAudios,
            clipAudioMaps.UploadedAudios,
            rootStageHandoff);
        EnsureFinalStageOutputSaved();
    }

    private void EnsureComfyDependencies(IReadOnlyList<ClipWithStages> clipsWithStages)
    {
        if (g.Features.Contains(Constants.LtxVideoFeatureFlag)
            || !clipsWithStages.SelectMany(c => c.Stages).Any(stage =>
                !string.IsNullOrWhiteSpace(stage.ClipControlNetLora)
                && VideoStageModelCompat.IsLtxV2VideoModel(stage.Model)))
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
            first.ClipLengthFromAudio && !first.ClipLengthFromControlNet,
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
            BuildPerClipAudioDetections(audioHandler, clips);
        IReadOnlyDictionary<int, WGNodeData> uploadedAudios =
            BuildPerClipUploadDetections(clips);
        audioHandler.PruneAceStepFunUnsavedTracks(clips);
        return new ClipAudioMaps(nativeAudio, clipAudios, uploadedAudios);
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
            AudioFile uploaded = g.GetUploadedAudioForClip(clip);
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
