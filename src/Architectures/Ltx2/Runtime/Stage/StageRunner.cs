using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution.Parameters;
using VideoStages.Execution;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal class StageRunner
{
    private readonly WorkflowGenerator _generator;
    private readonly LtxStageExecutor _stageExecutor;
    private readonly LtxStageGuideMediaResolver _guideMediaResolver;
    private readonly FrameRefResolver _frameRefResolver;
    private readonly StageSourceMediaResolver _sourceMediaResolver;
    private readonly StageFramePreparer _framePreparer;

    public StageRunner(
        WorkflowGenerator generator,
        LtxStageExecutor stageExecutor,
        LtxStageGuideMediaResolver guideMediaResolver,
        FrameRefResolver frameRefResolver)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _stageExecutor = stageExecutor ?? throw new ArgumentNullException(nameof(stageExecutor));
        _guideMediaResolver = guideMediaResolver
            ?? throw new ArgumentNullException(nameof(guideMediaResolver));
        _frameRefResolver = frameRefResolver
            ?? throw new ArgumentNullException(nameof(frameRefResolver));
        _sourceMediaResolver = new StageSourceMediaResolver(generator);
        _framePreparer = new StageFramePreparer(
            generator,
            _sourceMediaResolver,
            new PlannedStagePromptResolver(generator));
    }

    public void RunStage(
        StagePlan stage,
        int sectionId,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        ClipContext clipContext,
        bool requiresDedicatedOutput,
        RootPlan root)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(clipContext);
        ArgumentNullException.ThrowIfNull(root);
        if (_generator.CurrentMedia is null)
        {
            throw Invariant.Failure(
                $"stage {stage.StageId} has no input media.");
        }

        ClipPlan clip = clipContext.PlannedClip;
        if (stage.IsPassthrough
            && !root.StageTakesOverTextToVideoRoot(stage, clip))
        {
            RunPassthroughStage(stage, sectionId, clipContext);
            return;
        }

        using StageModelLoadScope modelScope = new(
            _generator,
            clip,
            stage,
            sectionId);

        StageFrame stageFrame = _framePreparer.Prepare(
            stage,
            sectionId,
            clipContext,
            requiresDedicatedOutput,
            root);
        RunLtxStage(guideReference, refStore, stageFrame);
    }

    private void RunLtxStage(
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        StageFrame stageFrame)
    {
        WorkflowGenerator.ImageToVideoGenInfo genInfo = stageFrame.GenInfo;
        WGNodeData sourceMedia = stageFrame.SourceMedia;
        LtxPostVideoChain postVideoChain = stageFrame.PostVideoChain;
        if (!Ltx2ArchitectureModule.IsLtx23VideoModel(genInfo.VideoModel)
            || (sourceMedia?.DataType != WGNodeData.DT_VIDEO
                && sourceMedia?.DataType != WGNodeData.DT_IMAGE))
        {
            throw Invariant.Failure(
                "the LTX stage input was neither decoded video nor image media.");
        }

        StagePlan stage = stageFrame.Stage;
        ClipContext clipContext = stageFrame.ClipContext;
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        ReferenceFramingMode referenceFraming =
            clipContext.PlannedClip.RequireLtx2Payload().ReferenceFraming;
        List<ResolvedFrameRef> frameRefs = _frameRefResolver.ResolveStageFrameRefs(
            clipContext.PlannedClip,
            stage,
            clipContext.Plan.Root.HostKind == HostRootKind.TextToVideo,
            refStore,
            postVideoChain,
            sourceMedia,
            clipContext.IncomingContinueHandleFrames);
        ResolvedFrameRef primaryGuideFrameRef = FrameRefResolver.ExtractPrimaryGuideFrameRef(frameRefs);
        frameRefs = FrameRefResolver.RemovePrimaryGuideFrameRef(frameRefs, primaryGuideFrameRef);
        bool reanchorsContinuityTail = clipContext.ReanchorsContinuityTail(stage);
        bool isContinuationTail = reanchorsContinuityTail && clipContext.IsFirstStage(stage);
        if (isContinuationTail)
        {
            // Guide preparation resizes its input, so preserve the stored continuity frame.
            primaryGuideFrameRef = new ResolvedFrameRef(
                clipContext.ContinuityTail.Duplicate(),
                new FrameRefPlan(
                    FrameRefSourceKind.Unknown,
                    RawSource: "Continue",
                    Base2EditStageIndex: null,
                    Frame: 1,
                    FrameRefEdge.Start,
                    Strength: 1.0,
                    UploadFileName: null,
                    InlineData: null),
                Strength: 1.0);
        }
        else if (reanchorsContinuityTail)
        {
            // A latent handoff does not preserve the opening frames at the new resolution.
            stageFrame.ContinuityAnchor = GuideMediaPreparation.Prepare(
                _generator,
                clipContext.ContinuityTail.Duplicate(),
                sourceMedia,
                scaleToSourceSize: true,
                referenceFraming: referenceFraming);
        }

        WGNodeData guideMedia = null;
        Func<WGNodeData> resolveFallbackGuide = null;
        if (primaryGuideFrameRef is not null)
        {
            guideMedia = PrimaryGuideIsStageInput(
                    primaryGuideFrameRef,
                    stageFrame.PriorOutputPath,
                    sourceMedia)
                ? ResolveDefaultLocalGuideMedia(sourceMedia, postVideoChain)
                : GuideMediaPreparation.Prepare(
                    _generator,
                    primaryGuideFrameRef.Image,
                    sourceMedia,
                    scaleToSourceSize: true,
                    referenceFraming: referenceFraming);
        }
        else
        {
            // Reinjection would overwrite the noise mask for the encoded footage's frame span.
            bool initVideoFootageIsStageInput =
                clipContext.PlannedClip.EntryMode == ArchitectureEntryMode.InitVideo
                && clipContext.IsFirstStage(stage)
                && payload.Guide.Kind == StageGuideReferenceKind.Generated
                && !payload.ImageReferenceWasExplicit;
            // Only clip 0/stage 0 treats the host image as its implicit frame-1 guide.
            bool refinesIncomingLatent = clipContext.Plan.Root.HostKind == HostRootKind.ImageToVideo
                && !payload.ImageReferenceWasExplicit
                && (clipContext.PlannedClip.ClipId != 0 || stage.ClipStageIndex != 0);
            if (!stageFrame.TakesOverTextToVideoRoot
                && frameRefs.Count == 0
                && !initVideoFootageIsStageInput
                && !refinesIncomingLatent)
            {
                if (PriorStageLatentIsReusable(
                        stage,
                        sourceMedia,
                        guideReference,
                        genInfo,
                        postVideoChain))
                {
                    resolveFallbackGuide = () => ResolveReinjectedGuideMedia(
                        guideReference,
                        stageFrame,
                        referenceFraming);
                }
                else if (guideReference?.Media is not null)
                {
                    guideMedia = ResolveReinjectedGuideMedia(
                        guideReference,
                        stageFrame,
                        referenceFraming);
                }
            }
        }
        _stageExecutor.RunStage(
            stageFrame,
            guideMedia,
            resolveFallbackGuide,
            frameRefs,
            primaryGuideFrameRef?.Strength ?? 1.0);
    }

    private bool PrimaryGuideIsStageInput(
        ResolvedFrameRef primaryGuideFrameRef,
        JArray priorOutputPath,
        WGNodeData sourceMedia)
    {
        if (primaryGuideFrameRef is null)
        {
            return false;
        }
        return (primaryGuideFrameRef.Image?.Path is JArray guidePath
                && priorOutputPath is not null
                && JToken.DeepEquals(guidePath, priorOutputPath))
            || FrameRefResolver.PrimaryGuideMatchesScaledSource(
                _generator,
                primaryGuideFrameRef.Image,
                sourceMedia);
    }

    private WGNodeData ResolveReinjectedGuideMedia(
        StageRefStore.StageRef guideReference,
        StageFrame stageFrame,
        ReferenceFramingMode referenceFraming)
    {
        WGNodeData sourceMedia = stageFrame.SourceMedia;
        Ltx2StagePayload payload = stageFrame.Stage.RequireLtx2Payload();
        bool guideIsStageInput = GuideReferenceIsStageInput(
            guideReference,
            stageFrame.PriorOutputPath,
            payload.Guide.Kind,
            payload.ImageReferenceWasExplicit,
            stageFrame.PostVideoChain);
        bool guideIsLiveOutput = _guideMediaResolver.IsLiveCurrentOutputReference(
            guideReference?.Media,
            stageFrame.PostVideoChain);
        WGNodeData authoredGuide = guideIsStageInput && !guideIsLiveOutput
            ? sourceMedia
            : _guideMediaResolver.ResolveGuideMedia(guideReference, stageFrame.PostVideoChain);
        if (guideIsStageInput && authoredGuide is not null)
        {
            // The detached decode may retain stale pre-resize metadata.
            authoredGuide.Width = sourceMedia.Width;
            authoredGuide.Height = sourceMedia.Height;
        }
        return GuideMediaPreparation.Prepare(
            _generator,
            authoredGuide,
            sourceMedia,
            scaleToSourceSize: true,
            referenceFraming: referenceFraming);
    }

    private bool GuideReferenceIsStageInput(
        StageRefStore.StageRef guideReference,
        JArray priorOutputPath,
        StageGuideReferenceKind guideKind,
        bool guideWasExplicit,
        LtxPostVideoChain postVideoChain)
    {
        if (guideReference?.Media?.Path is not JArray guidePath)
        {
            return false;
        }
        return guideKind == StageGuideReferenceKind.Generated && !guideWasExplicit
            || priorOutputPath is not null && JToken.DeepEquals(guidePath, priorOutputPath)
            || _guideMediaResolver.IsLiveCurrentOutputReference(
                guideReference.Media,
                postVideoChain);
    }

    private WGNodeData ResolveDefaultLocalGuideMedia(
        WGNodeData sourceMedia,
        LtxPostVideoChain postVideoChain)
    {
        if (postVideoChain is not null
            && _guideMediaResolver.IsLiveCurrentOutputReference(sourceMedia, postVideoChain))
        {
            WGNodeData detachedGuideVae = postVideoChain.CreateStageInputVae();
            return postVideoChain.CreateDetachedGuideMedia(detachedGuideVae);
        }

        return sourceMedia;
    }

    private bool PriorStageLatentIsReusable(
        StagePlan stage,
        WGNodeData sourceMedia,
        StageRefStore.StageRef guideReference,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        LtxPostVideoChain postVideoChain) =>
        stage.RequireLtx2Payload().Guide.Kind == StageGuideReferenceKind.Generated
        && postVideoChain?.CanReuseCurrentOutputAsStageInput(sourceMedia) == true
        && _guideMediaResolver.IsLiveCurrentOutputReference(guideReference?.Media, postVideoChain)
        && !string.IsNullOrWhiteSpace(guideReference?.Vae?.Compat?.ID)
        && guideReference.Vae.Compat.ID == genInfo.VideoModel?.ModelClass?.CompatClass?.ID;

    private void RunPassthroughStage(
        StagePlan stage,
        int sectionId,
        ClipContext clipContext)
    {
        LtxAudioReuseState.PrepareReusableAudio(_generator, clipContext, stage);
        LtxPostVideoChain postVideoChain =
            LtxPostVideoChain.TryCapture(_generator, clipContext, stage);
        _ = _sourceMediaResolver.Resolve(clipContext, stage, sectionId, postVideoChain);
    }

}
