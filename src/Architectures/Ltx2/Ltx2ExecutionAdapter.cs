using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class Ltx2ExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionProvider,
    IArchitectureHostPhaseParticipant
{
    private readonly RootMediaHandoff _rootHandoff = new(
        generator,
        "LTX");

    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

    public IArchitectureBoundaryAssembler BoundaryAssembler { get; } =
        new Ltx2BoundaryAssembler();

    public IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Ltx2RequestPreflight.Resolve(generator.Features, context.Plan);
    }

    public void ExecuteHostPhase(ArchitectureHostPhaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Phase == ArchitectureHostPhase.CapturePreCoreMedia)
        {
            _rootHandoff.CapturePreCoreMedia();
            return;
        }
        if (context.Phase == ArchitectureHostPhase.DropCoreOutput)
        {
            _rootHandoff.DropCoreOutput();
            return;
        }
        if (context.Phase == ArchitectureHostPhase.CaptureControlNetPreprocessors)
        {
            new LtxControlNetMediaNormalizer(generator).Normalize(
                ownsHostRoot: context.RootOwnerArchitectureId == ArchitectureId);
            return;
        }
        switch (context.Phase)
        {
            case ArchitectureHostPhase.CaptureBaseReference:
                new StageRefStore(generator).Capture(StageRefStore.StageKind.Base);
                break;
            case ArchitectureHostPhase.CaptureRefinerReference:
                new StageRefStore(generator).Capture(StageRefStore.StageKind.Refiner);
                break;
            case ArchitectureHostPhase.ApplyRootAudioMaskDimensions:
                new LtxAudioMaskResizer(
                    generator,
                    new RootVideoStageResizer(generator))
                    .ApplyRootAudioMaskDimensionsAfterNativeVideo();
                break;
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
            new LtxStageExecutor(generator, resizer),
            guideMediaResolver,
            new LtxClipRefResolver(generator, guideMediaResolver));
        StageSequenceRootSetup rootSetup = new(
            generator,
            stageRefStore,
            resizer);
        StageGuideReferenceState guideReferences = new(
            generator,
            stageRefStore);
        StageClipExecutor clipExecutor = new(
            generator,
            stageRefStore,
            stageRunner,
            audioTimelineExecutor,
            guideReferences,
            new BoundaryHandoffResolver(
                new ContinuityGuideBuilder(generator),
                new LtxBoundaryAudioCarryBuilder(generator)));
        StageSequenceRootSources rootSources;
        if (context.OwnsGeneratedRoot)
        {
            audioTimelineExecutor.PrepareRootAudio(
                context.Plan,
                context.AudioSources,
                context.RootPolicy);
            rootSources = rootSetup.Prepare(
                context.AudioSources,
                context.RootPolicy);
        }
        else
        {
            rootSources = rootSetup.Snapshot(context.AudioSources);
        }
        return new GenerationSession(
            generator,
            clipExecutor,
            rootSources,
            new VideoStageRunner(
                generator,
                context.Plan,
                "LTX"),
            context.Plan,
            context.Assembly,
            context.RootPolicy);
    }

    private sealed class GenerationSession(
        WorkflowGenerator generator,
        StageClipExecutor executor,
        StageSequenceRootSources rootSources,
        VideoStageRunner stageRunner,
        VideoExecutionPlan plan,
        TimelineAssemblySession assembly,
        RootExecutionPolicy rootPolicy) : IVideoGenerationSession
    {
        public ArchitectureId ArchitectureId =>
            Ltx2ArchitectureModule.ArchitectureId;

        public DecodedClipArtifact Execute(
            ArchitectureClipRuntimeContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            StageClipExecutionContext stageContext = new(
                context,
                plan,
                context.PreviousClipOutput?.ToHostMedia(generator),
                context.PreviousTimelineClipOutput?.ToHostMedia(generator),
                rootSources,
                assembly,
                rootPolicy);
            RuntimeArtifact output = executor.Execute(stageContext, stageRunner);
            return DecodedClipArtifact.FromRuntime(output, context.Clip);
        }

        public void Dispose()
        {
            stageRunner.Dispose();
        }
    }
}
