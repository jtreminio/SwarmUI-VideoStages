using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal sealed class LtxStageOrchestrator(
    WorkflowGenerator g,
    LtxStageExecutor stageExecutor,
    StageGuideMediaHelper stageGuideMediaHelper,
    LtxClipRefResolver clipRefResolver)
{
    internal bool TryRunLocalLtxPath(
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (!ShouldUseLocalLtxv2Path(genInfo, sourceMedia))
        {
            return false;
        }

        StageSpec stage = stageFrame.Stage;
        List<ResolvedClipRef> clipRefs = clipRefResolver.ResolveStageClipRefs(
            stageFrame.ClipContext.Clip,
            stage,
            refStore,
            postVideoChain,
            sourceMedia);
        ResolvedClipRef primaryGuideClipRef = LtxClipRefResolver.ExtractPrimaryGuideClipRef(clipRefs);
        clipRefs = LtxClipRefResolver.RemovePrimaryGuideClipRef(clipRefs, primaryGuideClipRef);
        if (stageFrame.ClipContext.ContinuityFrame is WGNodeData continuityFrame
            && stageFrame.ClipContext.IsFirstStage(stage))
        {
            // "continue" boundary: generate this clip with the previous clip's tail frames frozen as its
            // opening latent context (LTXVImgToVideoInplace encodes the whole batch). The sequence runner
            // only arms ContinuityFrame when the clip has no explicit first-frame ref, so this can only
            // ever displace the implicit image-to-video default ref.
            primaryGuideClipRef = new ResolvedClipRef(
                continuityFrame,
                new ImageRefSpec("Continue", Frame: 1, FromEnd: false, UploadFileName: null),
                Strength: 1.0);
        }
        double guideMergeStrength = primaryGuideClipRef?.Strength ?? 1.0;

        bool replacesTextToVideoRoot = stageFrame.ReplacesTextToVideoRoot;
        // A sourced clip's first stage samples its encoded footage directly (init-video
        // img2img); reinjecting that same footage as an i2v inplace guide would overwrite the
        // noise mask of every frame it spans. The official upscaler/V2V flows are encode-only.
        bool sourcedFootageIsStageInput = stageFrame.ClipContext.Clip.SourceVideo is not null
            && stageFrame.ClipContext.IsFirstStage(stage);
        bool skipGuideReinjection = primaryGuideClipRef is null
            && (replacesTextToVideoRoot
                || clipRefs is { Count: > 0 }
                || sourcedFootageIsStageInput
                || ShouldSkipGeneratedGuideReinjection(
                    stage,
                    sourceMedia,
                    guideReference,
                    genInfo,
                    postVideoChain));

        WGNodeData guideMedia = ResolveLocalGuideMedia(
            primaryGuideClipRef,
            skipGuideReinjection,
            sourceMedia,
            priorOutputPath,
            postVideoChain);

        stageExecutor.RunStage(
            genInfo,
            stageFrame,
            sourceMedia,
            guideMedia,
            skipGuideReinjection,
            postVideoChain,
            clipRefs,
            guideMergeStrength);
        return true;
    }

    private static bool ShouldUseLocalLtxv2Path(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia)
    {
        return VideoStageModelCompat.IsLtxV2VideoModel(genInfo.VideoModel)
            && (sourceMedia?.DataType == WGNodeData.DT_VIDEO
                || sourceMedia?.DataType == WGNodeData.DT_IMAGE);
    }

    private WGNodeData ResolveLocalGuideMedia(
        ResolvedClipRef primaryGuideClipRef,
        bool skipGuideReinjection,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (primaryGuideClipRef is null)
        {
            return ResolveDefaultLocalGuideMedia(skipGuideReinjection, sourceMedia, postVideoChain);
        }

        if (primaryGuideClipRef.Image?.Path is JArray guidePath
            && priorOutputPath is not null
            && JToken.DeepEquals(guidePath, priorOutputPath))
        {
            return ResolveDefaultLocalGuideMedia(skipGuideReinjection: false, sourceMedia, postVideoChain);
        }

        if (LtxClipRefResolver.PrimaryGuideMatchesScaledSource(g, primaryGuideClipRef.Image, sourceMedia))
        {
            return ResolveDefaultLocalGuideMedia(skipGuideReinjection: false, sourceMedia, postVideoChain);
        }

        return stageGuideMediaHelper.PrepareGuideMedia(primaryGuideClipRef.Image, sourceMedia, scaleToSourceSize: true);
    }

    private WGNodeData ResolveDefaultLocalGuideMedia(
        bool skipGuideReinjection,
        WGNodeData sourceMedia,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (skipGuideReinjection)
        {
            return null;
        }

        if (postVideoChain is not null
            && stageGuideMediaHelper.IsLiveCurrentOutputReference(sourceMedia, postVideoChain))
        {
            WGNodeData detachedGuideVae = postVideoChain.CreateStageInputVae();
            return postVideoChain.CreateDetachedGuideMedia(detachedGuideVae);
        }

        return sourceMedia;
    }

    private bool ShouldSkipGeneratedGuideReinjection(
        StageSpec stage,
        WGNodeData sourceMedia,
        StageRefStore.StageRef guideReference,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        LtxPostVideoChainCapture postVideoChain)
    {
        return stage.ImageReference == "Generated"
            && postVideoChain?.CanReuseCurrentOutputAsStageInput(sourceMedia) == true
            && stageGuideMediaHelper.IsLiveCurrentOutputReference(guideReference?.Media, postVideoChain)
            && !string.IsNullOrWhiteSpace(guideReference?.Vae?.Compat?.ID)
            && guideReference.Vae.Compat.ID == genInfo.VideoModel?.ModelClass?.CompatClass?.ID;
    }
}
