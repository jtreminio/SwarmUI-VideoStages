using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal sealed class LtxStageOrchestrator(
    WorkflowGenerator g,
    LtxStageExecutor stageExecutor,
    RootVideoStageHandoff rootVideoStageHandoff,
    StageGuideMediaHelper stageGuideMediaHelper,
    LtxClipRefResolver clipRefResolver)
{
    internal bool TryRunLocalLtxPath(
        JsonParser.StageSpec stage,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (!ShouldUseLocalLtxv2Path(genInfo, sourceMedia))
        {
            return false;
        }

        List<ResolvedClipRef> clipRefs = clipRefResolver.ResolveStageClipRefs(
            stage,
            refStore,
            postVideoChain,
            sourceMedia);
        ResolvedClipRef primaryGuideClipRef = LtxClipRefResolver.ExtractPrimaryGuideClipRef(clipRefs);
        clipRefs = LtxClipRefResolver.RemovePrimaryGuideClipRef(clipRefs, primaryGuideClipRef);
        double guideMergeStrength = primaryGuideClipRef?.Strength ?? 1.0;

        bool replacesTextToVideoRoot = rootVideoStageHandoff.ShouldReplaceTextToVideoRootStage(stage);
        bool skipGuideReinjection = primaryGuideClipRef is null
            && (replacesTextToVideoRoot
                || clipRefs is { Count: > 0 }
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
            stage,
            genInfo,
            stageFrame,
            sourceMedia,
            guideMedia,
            skipGuideReinjection,
            applySourceVideoLatent,
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
        JsonParser.StageSpec stage,
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
