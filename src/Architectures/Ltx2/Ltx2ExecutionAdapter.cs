using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// LTX-owned adapter for host phases, timeline-session construction, and root-media sizing.
/// </summary>
internal sealed class Ltx2ExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionFactoryProvider,
    IArchitectureHostPhaseParticipant,
    IArchitectureRootMediaResizerProvider
{
    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

    public void ExecuteHostPhase(ArchitectureHostPhaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Pipeline pipeline = BuildPipeline();
        switch (context.Phase)
        {
            case ArchitectureHostPhase.CaptureControlNetPreprocessors:
                new ControlNetCapture(generator).CaptureCoreVideoControlNetPreprocessors();
                break;
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
        StageRunner stageRunner = new(generator, pipeline.StageOrchestrator);
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
            new ContinuityGuideBuilder(generator),
            new LtxBoundaryAudioCarryBuilder(generator));
        return new Ltx2GenerationSessionFactory(
            generator,
            audioTimelineExecutor,
            rootSetup,
            guideReferences,
            clipExecutor);
    }

    public IArchitectureRootMediaResizer CreateRootMediaResizer() =>
        BuildPipeline().Resizer;

    private Pipeline BuildPipeline()
    {
        StageRefStore stageRefStore = new(generator);
        RootVideoStageHandoff handoff = new(generator, stageRefStore);
        RootVideoStageResizer resizer = new(generator, handoff);
        StageGuideMediaHelper guideMediaHelper = new(generator);
        Base2EditPublishedStageRefs base2Edit = new(generator);
        LtxAudioInjector audioInjector = new(generator, resizer);
        LtxAudioMaskResizer audioMaskResizer = new(generator, resizer);
        LtxStageExecutor stageExecutor = new(generator, resizer);
        LtxClipRefResolver clipRefResolver = new(
            generator,
            guideMediaHelper,
            base2Edit);
        LtxStageOrchestrator stageOrchestrator = new(
            generator,
            stageExecutor,
            guideMediaHelper,
            clipRefResolver);
        return new(
            stageRefStore,
            handoff,
            resizer,
            base2Edit,
            audioInjector,
            audioMaskResizer,
            stageOrchestrator);
    }

    private readonly record struct Pipeline(
        StageRefStore StageRefStore,
        RootVideoStageHandoff Handoff,
        RootVideoStageResizer Resizer,
        Base2EditPublishedStageRefs Base2Edit,
        LtxAudioInjector AudioInjector,
        LtxAudioMaskResizer AudioMaskResizer,
        LtxStageOrchestrator StageOrchestrator);
}
