using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class Ltx2ExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionFactoryProvider,
    IArchitectureHostPhaseParticipant
{
    private readonly RootMediaHandoff _rootHandoff = new(
        generator,
        "LTX");

    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

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
        Pipeline pipeline = BuildPipeline();
        switch (context.Phase)
        {
            case ArchitectureHostPhase.CaptureBaseReference:
                pipeline.StageRefStore.Capture(StageRefStore.StageKind.Base);
                break;
            case ArchitectureHostPhase.CaptureRefinerReference:
                pipeline.StageRefStore.Capture(StageRefStore.StageKind.Refiner);
                break;
            case ArchitectureHostPhase.ApplyRootAudioMaskDimensions:
                pipeline.AudioMaskResizer.ApplyRootAudioMaskDimensionsAfterNativeVideo();
                break;
        }
    }

    public IArchitectureGenerationSessionFactory CreateFactory()
    {
        Pipeline pipeline = BuildPipeline();
        AudioTimelineExecutor audioTimelineExecutor =
            new(generator, pipeline.AudioInjector);
        StageRunner stageRunner = new(
            generator,
            pipeline.StageExecutor,
            pipeline.GuideMediaResolver,
            pipeline.ClipRefResolver);
        StageSequenceRootSetup rootSetup = new(
            generator,
            pipeline.StageRefStore,
            pipeline.Resizer);
        StageGuideReferenceState guideReferences = new(
            generator,
            pipeline.StageRefStore);
        StageClipExecutor clipExecutor = new(
            generator,
            pipeline.StageRefStore,
            stageRunner,
            audioTimelineExecutor,
            guideReferences,
            new BoundaryHandoffResolver(
                new ContinuityGuideBuilder(generator),
                new LtxBoundaryAudioCarryBuilder(generator)));
        return new GenerationSessionFactory(
            generator,
            audioTimelineExecutor,
            rootSetup,
            clipExecutor);
    }

    private Pipeline BuildPipeline()
    {
        StageRefStore stageRefStore = new(generator);
        RootVideoStageResizer resizer = new(generator);
        LtxStageGuideMediaResolver guideMediaResolver = new(generator);
        LtxAudioInjector audioInjector = new(generator, resizer);
        LtxAudioMaskResizer audioMaskResizer = new(generator, resizer);
        LtxStageExecutor stageExecutor = new(generator, resizer);
        LtxClipRefResolver clipRefResolver = new(
            generator,
            guideMediaResolver);
        return new(
            stageRefStore,
            resizer,
            audioInjector,
            audioMaskResizer,
            stageExecutor,
            guideMediaResolver,
            clipRefResolver);
    }

    private readonly record struct Pipeline(
        StageRefStore StageRefStore,
        RootVideoStageResizer Resizer,
        LtxAudioInjector AudioInjector,
        LtxAudioMaskResizer AudioMaskResizer,
        LtxStageExecutor StageExecutor,
        LtxStageGuideMediaResolver GuideMediaResolver,
        LtxClipRefResolver ClipRefResolver);

    private sealed class GenerationSessionFactory(
        WorkflowGenerator generator,
        AudioTimelineExecutor audioTimeline,
        StageSequenceRootSetup rootSetup,
        StageClipExecutor clipExecutor) : IArchitectureGenerationSessionFactory
    {
        private StageSequenceRootSources _rootSources;

        public ArchitectureId ArchitectureId =>
            Ltx2ArchitectureModule.ArchitectureId;

        public IArchitectureBoundaryAssembler BoundaryAssembler { get; } =
            new Ltx2BoundaryAssembler();

        public void PrepareTimeline(
            ArchitectureTimelinePreparationContext context)
        {
            if (context.OwnsGeneratedRoot)
            {
                audioTimeline.PrepareRootAudio(
                    context.Plan,
                    context.AudioSources,
                    context.RootPolicy);
                _rootSources = rootSetup.Prepare(
                    context.AudioSources,
                    context.RootPolicy);
            }
            else
            {
                _rootSources = rootSetup.Snapshot(context.AudioSources);
            }
        }

        public IVideoGenerationSession CreateSession(
            ArchitectureTimelineSessionContext context)
        {
            if (_rootSources is null)
            {
                throw VideoStagesInvariant.Failure(
                    "The LTX timeline runtime was not prepared before session creation.");
            }
            return new GenerationSession(
                generator,
                clipExecutor,
                _rootSources,
                new StageHostExecutionScope(
                    generator,
                    context.Plan),
                context.Plan,
                context.Assembly,
                context.RootPolicy);
        }
    }

    private sealed class GenerationSession(
        WorkflowGenerator generator,
        StageClipExecutor executor,
        StageSequenceRootSources rootSources,
        StageHostExecutionScope hostScope,
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
                hostScope,
                rootPolicy);
            RuntimeArtifact output = executor.Execute(stageContext);
            return DecodedClipArtifact.FromRuntime(output, context.Clip);
        }

        public void Dispose()
        {
            hostScope.Dispose();
        }
    }
}
