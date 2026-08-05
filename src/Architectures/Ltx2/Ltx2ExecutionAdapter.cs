using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class Ltx2ExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionProvider
{
    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

    public IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Ltx2RequestPreflight.Resolve(generator.Features, context.Plan);
    }

    public void CaptureControlNetPreprocessors(bool ownsHostRoot) =>
        new LtxControlNetMediaNormalizer(generator).Normalize(ownsHostRoot);

    public void CaptureBaseReference(VideoExecutionPlan plan) =>
        CaptureIfReferenced(plan, StageRefStore.StageKind.Base);

    public void CaptureRefinerReference(VideoExecutionPlan plan) =>
        CaptureIfReferenced(plan, StageRefStore.StageKind.Refiner);

    public void ApplyRootAudioMaskDimensions() =>
        new LtxAudioMaskResizer(
            generator,
            new RootVideoStageResizer(generator))
            .ApplyRootAudioMaskDimensionsAfterNativeVideo();

    // Capturing reserves a host node for the request, so capture only references the plan can use.
    private void CaptureIfReferenced(VideoExecutionPlan plan, StageRefStore.StageKind kind)
    {
        if (plan.Root.DiscardsTextToVideoRoot)
        {
            return;
        }
        (StageGuideReferenceKind guide, ImageReferenceSourceKind reference) = kind switch
        {
            StageRefStore.StageKind.Base =>
                (StageGuideReferenceKind.Base, ImageReferenceSourceKind.Base),
            StageRefStore.StageKind.Refiner =>
                (StageGuideReferenceKind.Refiner, ImageReferenceSourceKind.Refiner),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        bool referenced = plan.Clips
            .SelectMany(clip => clip.Stages)
            .Select(stage => stage.ArchitecturePayload as Ltx2StagePayload)
            .Where(payload => payload is not null)
            .Any(payload => payload.Guide?.Kind == guide
                || (!payload.FrameReferences.IsDefaultOrEmpty
                    && payload.FrameReferences.Any(
                        frameRef => frameRef.SourceKind == reference)));
        if (referenced)
        {
            new StageRefStore(generator).Capture(kind);
        }
    }

    public IVideoGenerationSession CreateSession(
        ArchitectureTimelineSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        StageRefStore stageRefStore = new(generator);
        RootVideoStageResizer resizer = new(generator);
        LtxAudioInjector audioInjector = new(generator, resizer);
        LtxStageGuideMediaResolver guideMediaResolver = new(generator);
        AudioTimelineExecutor audioTimelineExecutor =
            new(generator, audioInjector);
        StageRunner stageRunner = new(
            generator,
            new LtxStageExecutor(generator, resizer, context.RootAdoption),
            guideMediaResolver,
            new LtxClipRefResolver(generator, guideMediaResolver));
        StageSequenceRootSetup rootSetup = new(
            generator,
            stageRefStore,
            resizer);
        StageGuideReferenceState guideReferences = new(
            generator,
            stageRefStore,
            context.Plan.Root);
        StageSequenceRootSources rootSources;
        if (context.OwnsGeneratedRoot)
        {
            audioTimelineExecutor.PrepareRootAudio(
                context.Plan,
                context.AudioSources,
                context.Plan.Root);
            rootSources = rootSetup.Prepare(
                context.AudioSources,
                context.Plan.Root);
        }
        else
        {
            rootSources = rootSetup.Snapshot(context.AudioSources, context.Plan.Root);
        }
        return new Ltx2GenerationSession(
            generator,
            stageRefStore,
            stageRunner,
            audioTimelineExecutor,
            guideReferences,
            new BoundaryHandoffResolver(
                generator,
                new ContinuityGuideBuilder(generator)),
            rootSources,
            context);
    }
}
