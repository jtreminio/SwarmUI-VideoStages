using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// LTX-owned adapter for host phases, timeline-session construction, and root-media sizing.
/// </summary>
internal sealed class Ltx2ExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionFactoryProvider,
    IArchitectureHostPhaseParticipant
{
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
            case ArchitectureHostPhase.CapturePreCoreMedia:
                pipeline.Handoff.CapturePreCoreVideoMedia();
                break;
            case ArchitectureHostPhase.DropCoreOutput:
                pipeline.Handoff.DropCoreImageToVideoOutput();
                break;
            case ArchitectureHostPhase.ApplyRootAudioMaskDimensions:
                pipeline.AudioMaskResizer.ApplyRootAudioMaskDimensionsAfterNativeVideo();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(context));
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
            pipeline.StageRefStore,
            pipeline.Base2Edit);
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
        RootVideoStageHandoff handoff = new(generator, stageRefStore);
        RootVideoStageResizer resizer = new(generator, handoff);
        LtxStageGuideMediaResolver guideMediaResolver = new(generator);
        Base2EditPublishedStageRefs base2Edit = new(generator);
        LtxAudioInjector audioInjector = new(generator, resizer);
        LtxAudioMaskResizer audioMaskResizer = new(generator, resizer);
        LtxStageExecutor stageExecutor = new(generator, resizer);
        LtxClipRefResolver clipRefResolver = new(
            generator,
            guideMediaResolver,
            base2Edit);
        return new(
            stageRefStore,
            handoff,
            resizer,
            base2Edit,
            audioInjector,
            audioMaskResizer,
            stageExecutor,
            guideMediaResolver,
            clipRefResolver);
    }

    private readonly record struct Pipeline(
        StageRefStore StageRefStore,
        RootVideoStageHandoff Handoff,
        RootVideoStageResizer Resizer,
        Base2EditPublishedStageRefs Base2Edit,
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
                throw new InvalidOperationException(
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
            if (context.Clip.ArchitecturePayload is not Ltx2ClipPayload)
            {
                throw new InvalidOperationException(
                    $"Clip {context.Clip.ClipId} has no LTX architecture payload.");
            }
            StageClipExecutionContext stageContext = new(
                context,
                plan,
                ToNodeData(context.PreviousClipOutput),
                ToNodeData(context.PreviousTimelineClipOutput),
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

        private WGNodeData ToNodeData(DecodedClipArtifact artifact)
        {
            if (artifact is null)
            {
                return null;
            }
            WGNodeData media = new(
                artifact.Video.ToPath(),
                generator,
                WGNodeData.DT_VIDEO,
                null)
            {
                Width = artifact.Width,
                Height = artifact.Height,
                Frames = artifact.Frames,
                FPS = artifact.FramesPerSecond,
            };
            if (artifact.Audio is not null)
            {
                media.AttachedAudio = new(
                    artifact.Audio.ToPath(),
                    generator,
                    WGNodeData.DT_AUDIO,
                    null);
            }
            return media;
        }
    }
}
