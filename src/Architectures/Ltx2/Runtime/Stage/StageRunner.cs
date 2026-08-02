using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal class StageRunner
{
    private readonly WorkflowGenerator _generator;
    private readonly LtxStageExecutor _stageExecutor;
    private readonly LtxStageGuideMediaResolver _guideMediaResolver;
    private readonly LtxClipRefResolver _clipRefResolver;
    private readonly StageUpscaleGraphBuilder _upscaleGraphBuilder;
    private readonly StageFramePreparer _framePreparer;
    private readonly IcLoraStageInputResolver _icLoraStageInputResolver;

    public StageRunner(
        WorkflowGenerator generator,
        LtxStageExecutor stageExecutor,
        LtxStageGuideMediaResolver guideMediaResolver,
        LtxClipRefResolver clipRefResolver)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _stageExecutor = stageExecutor ?? throw new ArgumentNullException(nameof(stageExecutor));
        _guideMediaResolver = guideMediaResolver
            ?? throw new ArgumentNullException(nameof(guideMediaResolver));
        _clipRefResolver = clipRefResolver
            ?? throw new ArgumentNullException(nameof(clipRefResolver));
        _upscaleGraphBuilder = new StageUpscaleGraphBuilder(generator);
        _framePreparer = new StageFramePreparer(
            generator,
            _upscaleGraphBuilder,
            new PlannedStagePromptResolver(generator));
        _icLoraStageInputResolver = new IcLoraStageInputResolver(generator);
    }

    public RuntimeArtifact RunStage(
        StagePlan stage,
        int sectionId,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        ClipContext clipContext,
        bool requiresDedicatedOutput,
        RootExecutionPolicy rootPolicy)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(clipContext);
        ArgumentNullException.ThrowIfNull(rootPolicy);
        if (_generator.CurrentMedia is null)
        {
            throw VideoStagesInvariant.Failure(
                $"VideoStages: stage {stage.StageId} has no input media.");
        }

        ClipPlan clip = clipContext.PlannedClip;
        if (stage.IsPassthrough
            && !rootPolicy.ReplacesTextToVideoRootStage(stage, clip))
        {
            RunPassthroughStage(stage, sectionId, clipContext);
            return CaptureArtifact(_generator, stage);
        }

        using ParamSnapshot promptLoraScope = PromptParser.ApplyLoraScope(
            _generator.UserInput,
            clip.ClipId,
            sectionId);
        using ParamSnapshot loraScope = LoraParams.ApplyNormalLoras(
            _generator.UserInput,
            stage.Core.Loras);

        StageFrame stageFrame = _framePreparer.Prepare(
            stage,
            sectionId,
            clipContext,
            requiresDedicatedOutput,
            rootPolicy);
        WorkflowGenerator.ImageToVideoGenInfo genInfo = stageFrame.GenInfo;
        Action<WorkflowGenerator.ImageToVideoGenInfo> applyIcLora = currentGenInfo =>
        {
            WGNodeData incomingMedia = _icLoraStageInputResolver.Resolve(stageFrame);
            bool needsCrop = new IcLoraApplicator(_generator).ApplyIcLoras(
                currentGenInfo,
                clip,
                stage,
                clip.Frames,
                incomingMedia);
            if (needsCrop)
            {
                stageFrame.NeedsCropGuidesAfterSampler = true;
            }
            new IcLoraAudioReferenceApplicator(_generator).ApplyAudioReferenceTokens(
                currentGenInfo,
                clip,
                stageFrame,
                incomingMedia);
        };

        RunLtxStage(guideReference, refStore, stageFrame, applyIcLora);
        return CaptureArtifact(_generator, stage);
    }

    internal static RuntimeArtifact CaptureArtifact(WorkflowGenerator generator, StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        RuntimeArtifact output = RuntimeArtifact.Capture(generator, bridge);
        if (!output.HasMedia)
        {
            throw VideoStagesInvariant.Failure(
                $"VideoStages: stage {stage.StageId} did not produce a video artifact.");
        }
        return output;
    }

    private void RunLtxStage(
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        StageFrame stageFrame,
        Action<WorkflowGenerator.ImageToVideoGenInfo> applyIcLora)
    {
        WorkflowGenerator.ImageToVideoGenInfo genInfo = stageFrame.GenInfo;
        WGNodeData sourceMedia = stageFrame.SourceMedia;
        LtxPostVideoChainCapture postVideoChain = stageFrame.PostVideoChain;
        if (!Ltx2ArchitectureModule.IsLtxV2VideoModel(genInfo.VideoModel)
            || (sourceMedia?.DataType != WGNodeData.DT_VIDEO
                && sourceMedia?.DataType != WGNodeData.DT_IMAGE))
        {
            throw VideoStagesInvariant.Failure(
                "VideoStages: the LTX stage input was neither decoded video nor image media.");
        }

        StagePlan stage = stageFrame.Stage;
        ClipContext clipContext = stageFrame.ClipContext;
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        ReferenceFramingMode referenceFraming =
            clipContext.PlannedClip.RequireLtx2Payload().ReferenceFraming;
        List<ResolvedClipRef> clipRefs = _clipRefResolver.ResolveStageClipRefs(
            clipContext.PlannedClip,
            stage,
            clipContext.Plan.Root.HostKind == HostRootKind.TextToVideoRoot,
            refStore,
            postVideoChain,
            sourceMedia);
        ResolvedClipRef primaryGuideClipRef = LtxClipRefResolver.ExtractPrimaryGuideClipRef(clipRefs);
        clipRefs = LtxClipRefResolver.RemovePrimaryGuideClipRef(clipRefs, primaryGuideClipRef);
        bool reanchorsContinuityTail = clipContext.ReanchorsContinuityTail(stage);
        bool isContinuationTail = reanchorsContinuityTail && clipContext.IsFirstStage(stage);
        if (isContinuationTail)
        {
            // Guide preparation resizes its input, so preserve the stored continuity frame.
            primaryGuideClipRef = new ResolvedClipRef(
                clipContext.ContinuityFrame.Duplicate(),
                new ImageReferencePlan(
                    ImageReferenceSourceKind.Unknown,
                    RawSource: "Continue",
                    Base2EditStageIndex: null,
                    Frame: 1,
                    ImageReferenceFrameEdge.Start,
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
                clipContext.ContinuityFrame.Duplicate(),
                sourceMedia,
                scaleToSourceSize: true,
                referenceFraming: referenceFraming);
        }

        StageInputCase inputCase = StageInputDispatcher.Resolve(new StageInputFacts(
            HasPrimaryGuide: primaryGuideClipRef is not null,
            PrimaryGuideIsStageInput: PrimaryGuideIsStageInput(
                primaryGuideClipRef,
                stageFrame.PriorOutputPath,
                sourceMedia),
            IsContinuationTail: isContinuationTail,
            HasOtherFrameReferences: clipRefs is { Count: > 0 },
            ReplacesTextToVideoRoot: stageFrame.ReplacesTextToVideoRoot,
            // Reinjection would overwrite the noise mask for the encoded footage's frame span.
            InitVideoFootageIsStageInput: clipContext.PlannedClip.HasInitVideo
                && clipContext.IsFirstStage(stage)
                && payload.Guide.Kind == StageGuideReferenceKind.Generated
                && !payload.ImageReferenceWasExplicit,
            // Only clip 0/stage 0 treats the host image as its implicit frame-1 guide.
            RefinesIncomingLatent: clipContext.Plan.Root.HostKind == HostRootKind.ImageToVideo
                && !payload.ImageReferenceWasExplicit
                && (clipContext.PlannedClip.ClipId != 0 || stage.ClipStageIndex != 0),
            PriorStageLatentIsReusable: PriorStageLatentIsReusable(
                stage,
                sourceMedia,
                guideReference,
                genInfo,
                postVideoChain),
            HasGuide: primaryGuideClipRef?.Image is not null || guideReference?.Media is not null));

        WGNodeData guideMedia = ResolveGuideMedia(
            inputCase,
            primaryGuideClipRef,
            guideReference,
            stageFrame,
            referenceFraming);
        Func<WGNodeData> resolveFallbackGuide = inputCase == StageInputCase.PriorStageLatentReuse
            ? () => ResolveReinjectedGuideMedia(
                guideReference,
                stageFrame,
                referenceFraming)
            : null;
        _stageExecutor.RunStage(
            genInfo,
            stageFrame,
            sourceMedia,
            guideMedia,
            StageInputDispatcher.SkipsGuideReinjection(inputCase),
            resolveFallbackGuide,
            postVideoChain,
            applyIcLora,
            clipRefs,
            primaryGuideClipRef?.Strength ?? 1.0);
    }

    private WGNodeData ResolveGuideMedia(
        StageInputCase inputCase,
        ResolvedClipRef primaryGuideClipRef,
        StageRefStore.StageRef guideReference,
        StageFrame stageFrame,
        ReferenceFramingMode referenceFraming) => inputCase switch
        {
            StageInputCase.PrimaryGuideIsStageInput =>
                ResolveDefaultLocalGuideMedia(stageFrame.SourceMedia, stageFrame.PostVideoChain),
            StageInputCase.ContinuationTail or StageInputCase.AuthoredGuideReference =>
                GuideMediaPreparation.Prepare(
                    _generator,
                    primaryGuideClipRef.Image,
                    stageFrame.SourceMedia,
                    scaleToSourceSize: true,
                    referenceFraming: referenceFraming),
            StageInputCase.GuideReinjection =>
                ResolveReinjectedGuideMedia(
                    guideReference,
                    stageFrame,
                    referenceFraming),
            _ => null,
        };

    private bool PrimaryGuideIsStageInput(
        ResolvedClipRef primaryGuideClipRef,
        JArray priorOutputPath,
        WGNodeData sourceMedia)
    {
        if (primaryGuideClipRef is null)
        {
            return false;
        }
        return (primaryGuideClipRef.Image?.Path is JArray guidePath
                && priorOutputPath is not null
                && JToken.DeepEquals(guidePath, priorOutputPath))
            || LtxClipRefResolver.PrimaryGuideMatchesScaledSource(
                _generator,
                primaryGuideClipRef.Image,
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
        LtxPostVideoChainCapture postVideoChain)
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
        LtxPostVideoChainCapture postVideoChain)
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
        LtxPostVideoChainCapture postVideoChain) =>
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
        LtxPostVideoChainCapture postVideoChain =
            LtxPostVideoChainCapture.TryCapture(_generator, clipContext, stage);
        _ = _upscaleGraphBuilder.Apply(clipContext, stage, sectionId, postVideoChain);
    }

}
