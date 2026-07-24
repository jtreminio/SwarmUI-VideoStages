using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxStageOrchestrator(
    WorkflowGenerator g,
    LtxStageExecutor stageExecutor,
    StageGuideMediaHelper stageGuideMediaHelper,
    LtxClipRefResolver clipRefResolver)
{
    internal void RunLtxPath(
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (!Ltx2ModelCompatibility.IsLtxV2VideoModel(genInfo.VideoModel)
            || (sourceMedia?.DataType != WGNodeData.DT_VIDEO
                && sourceMedia?.DataType != WGNodeData.DT_IMAGE))
        {
            throw new SwarmUserErrorException(
                "VideoStages: the LTX execution plan reached an invalid LTX stage input. "
                + "Regenerate after updating the timeline.");
        }

        StagePlan stage = stageFrame.Stage;
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        List<ResolvedClipRef> clipRefs = clipRefResolver.ResolveStageClipRefs(
            stageFrame.ClipContext.PlannedClip,
            stage,
            stageFrame.ClipContext.Plan.Root.HostKind == HostRootKind.TextToVideoRoot,
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
                new ImageReferencePlan(
                    Index: -1,
                    ImageReferenceSourceKind.Unknown,
                    RawSource: "Continue",
                    Base2EditStageIndex: null,
                    Frame: 1,
                    ImageReferenceFrameOrigin.Start,
                    Strength: 1.0,
                    UploadFileName: null,
                    InlineData: null),
                Strength: 1.0);
        }
        double guideMergeStrength = primaryGuideClipRef?.Strength ?? 1.0;

        bool replacesTextToVideoRoot = stageFrame.ReplacesTextToVideoRoot;
        // A sourced clip's first stage samples its encoded footage directly (init-video
        // img2img); reinjecting that same footage as an i2v inplace guide would overwrite the
        // noise mask of every frame it spans. The official upscaler/V2V flows are encode-only.
        bool sourcedFootageIsStageInput = stageFrame.ClipContext.PlannedClip.IsSourced
            && stageFrame.ClipContext.IsFirstStage(stage);
        bool implicitSourcedGuide = sourcedFootageIsStageInput
            && payload.Guide.Kind == GuideReferenceKind.Generated
            && !payload.Core.ImageReferenceWasExplicit;
        bool implicitHostGuideOutsideOpeningStage =
            stageFrame.ClipContext.Plan.Root.HostKind == HostRootKind.ImageToVideo
            && !payload.Core.ImageReferenceWasExplicit
            && (stageFrame.ClipContext.PlannedClip.ClipId != 0
                || stage.ClipStageIndex != 0);
        // The host's incoming image is the implicit frame-1 guide for clip 0/stage 0 only.
        // Later defaulted stages refine their incoming latent directly. Authored ImageReference
        // selectors and authored frame refs remain eligible for guide construction.
        bool skipGuideReinjection = primaryGuideClipRef is null
            && (replacesTextToVideoRoot
                || clipRefs is { Count: > 0 }
                || implicitSourcedGuide
                || implicitHostGuideOutsideOpeningStage
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
            guideReference,
            payload.Guide.Kind,
            payload.Core.ImageReferenceWasExplicit,
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
    }

    private WGNodeData ResolveLocalGuideMedia(
        ResolvedClipRef primaryGuideClipRef,
        bool skipGuideReinjection,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        StageRefStore.StageRef guideReference,
        GuideReferenceKind guideKind,
        bool guideWasExplicit,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (primaryGuideClipRef is null)
        {
            if (skipGuideReinjection)
            {
                return null;
            }
            bool guideIsStageInput = GuideReferenceIsStageInput(
                guideReference,
                priorOutputPath,
                guideKind,
                guideWasExplicit,
                postVideoChain);
            bool guideIsLiveOutput = stageGuideMediaHelper.IsLiveCurrentOutputReference(
                guideReference?.Media,
                postVideoChain);
            WGNodeData authoredGuide = guideIsStageInput && !guideIsLiveOutput
                ? sourceMedia
                : stageGuideMediaHelper.ResolveGuideMedia(
                    guideReference,
                    postVideoChain);
            if (guideIsStageInput && authoredGuide is not null)
            {
                // A detached decode of the stage-input latent has the same rendered dimensions as
                // the prepared source even when its marker still carries pre-resize metadata.
                authoredGuide.Width = sourceMedia.Width;
                authoredGuide.Height = sourceMedia.Height;
            }
            return stageGuideMediaHelper.PrepareGuideMedia(
                authoredGuide,
                sourceMedia,
                scaleToSourceSize: true);
        }

        if (primaryGuideClipRef.Image?.Path is JArray guidePath
            && priorOutputPath is not null
            && JToken.DeepEquals(guidePath, priorOutputPath))
        {
            return ResolveDefaultLocalGuideMedia(sourceMedia, postVideoChain);
        }

        if (LtxClipRefResolver.PrimaryGuideMatchesScaledSource(g, primaryGuideClipRef.Image, sourceMedia))
        {
            return ResolveDefaultLocalGuideMedia(sourceMedia, postVideoChain);
        }

        return stageGuideMediaHelper.PrepareGuideMedia(primaryGuideClipRef.Image, sourceMedia, scaleToSourceSize: true);
    }

    private bool GuideReferenceIsStageInput(
        StageRefStore.StageRef guideReference,
        JArray priorOutputPath,
        GuideReferenceKind guideKind,
        bool guideWasExplicit,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (guideReference?.Media?.Path is not JArray guidePath)
        {
            return false;
        }
        return guideKind == GuideReferenceKind.Generated && !guideWasExplicit
            || priorOutputPath is not null && JToken.DeepEquals(guidePath, priorOutputPath)
            || stageGuideMediaHelper.IsLiveCurrentOutputReference(
                guideReference.Media,
                postVideoChain);
    }

    private WGNodeData ResolveDefaultLocalGuideMedia(
        WGNodeData sourceMedia,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (postVideoChain is not null
            && stageGuideMediaHelper.IsLiveCurrentOutputReference(sourceMedia, postVideoChain))
        {
            WGNodeData detachedGuideVae = postVideoChain.CreateStageInputVae();
            return postVideoChain.CreateDetachedGuideMedia(detachedGuideVae);
        }

        return sourceMedia;
    }

    private bool ShouldSkipGeneratedGuideReinjection(
        StagePlan stage,
        WGNodeData sourceMedia,
        StageRefStore.StageRef guideReference,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        LtxPostVideoChainCapture postVideoChain)
    {
        return stage.RequireLtx2Payload().Guide.Kind == GuideReferenceKind.Generated
            && postVideoChain?.CanReuseCurrentOutputAsStageInput(sourceMedia) == true
            && stageGuideMediaHelper.IsLiveCurrentOutputReference(guideReference?.Media, postVideoChain)
            && !string.IsNullOrWhiteSpace(guideReference?.Vae?.Compat?.ID)
            && guideReference.Vae.Compat.ID == genInfo.VideoModel?.ModelClass?.CompatClass?.ID;
    }
}
