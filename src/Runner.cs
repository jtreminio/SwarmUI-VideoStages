using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.LTX2;

namespace VideoStages;

// Orchestration map — phases run in priority order during WorkflowGenerator.Generate.
// Registrations live in VideoStagesExtension.OnInit; numeric priorities in Constants.WorkflowStepPriority.
//
// #  Pri    Phase                                         Reads                                          Writes / clears
// -  -----  --------------------------------------------  ---------------------------------------------  ----------------------------------------------------
// 1  -5.9   CaptureCoreVideoControlNetPreprocessors       —                                              writes videostages.controlnet.fullimage.{i}
// 2  -4.2   CaptureBase                                   —                                              writes StageRefStore.Base
// 3   5.9   CaptureRefiner                                —                                              writes StageRefStore.Refiner
// 4  10.95  CapturePreCoreVideoMedia                      —                                              writes StageRefStore.PreRootVideo,
//                                                                                                                videostages.pre-core-node-ids
// 5  11.05  DropCoreImageToVideoOutput                    StageRefStore.PreRootVideo,                    clears both above
//                                                         videostages.pre-core-node-ids
// 6  11.4   ApplyRootAudioMaskDimensionsAfterNativeVideo  —                                              —
// 7  11.5   RunConfiguredStages                           StageRefStore.Base/Refiner,                    writes StageRefStore.Generated (intra-phase reference only)
//                                                         videostages.controlnet.fullimage.{i}
//
// Non-phase entry points (NOT registered as workflow steps — called from outside the pipeline):
//   TryInjectLtxAudio         Tests/AudioInjectionTests — unit-level audio injection coverage.
//   GetRootVideoStageResizer  RootVideoStageResizer.RegisterHandlers — static AltImageToVideo handlers.
public static class Runner
{
    public static void CaptureCoreVideoControlNetPreprocessors(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        new ControlNetCapture(g).CaptureCoreVideoControlNetPreprocessors();
    }

    public static void CaptureBase(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g) || !HasConfiguredStages(g))
        {
            return;
        }

        StageRefStore stageRefStore = new(g);
        stageRefStore.Capture(StageRefStore.StageKind.Base);
    }

    public static void CaptureRefiner(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g) || !HasConfiguredStages(g))
        {
            return;
        }

        StageRefStore stageRefStore = new(g);
        stageRefStore.Capture(StageRefStore.StageKind.Refiner);
    }

    public static void CapturePreCoreVideoMedia(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        StageRefStore stageRefStore = new(g);
        RootVideoStageHandoff rootVideoStageHandoff = new(g, stageRefStore);
        rootVideoStageHandoff.CapturePreCoreVideoMedia();
    }

    public static void DropCoreImageToVideoOutput(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        StageRefStore stageRefStore = new(g);
        RootVideoStageHandoff rootVideoStageHandoff = new(g, stageRefStore);
        rootVideoStageHandoff.DropCoreImageToVideoOutput();
    }

    public static void ApplyRootAudioMaskDimensionsAfterNativeVideo(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        BuildPipeline(g).LtxManager.ApplyRootAudioMaskDimensionsAfterNativeVideo();
    }

    public static void RunConfiguredStages(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        Pipeline pipeline = BuildPipeline(g);
        AudioHandler audioHandler = new(g);
        AudioTimelineExecutor audioTimelineExecutor = new(g, pipeline.Handoff, pipeline.LtxManager, audioHandler);
        MultiClipParallelMerger multiClipParallelMerger = new(g);
        TimelineAssembler timelineAssembler = new(g, multiClipParallelMerger);
        StageRunner stageRunner = new(g, pipeline.LtxManager);
        StageSequenceRunner stageSequenceRunner = new(
            g,
            pipeline.StageRefStore,
            stageRunner,
            pipeline.Base2Edit,
            pipeline.Resizer,
            timelineAssembler,
            pipeline.LtxManager,
            audioTimelineExecutor);
        VideoStagesCoordinator coordinator = new(
            g,
            pipeline.Handoff,
            stageSequenceRunner,
            audioTimelineExecutor);
        coordinator.RunConfiguredStages();
    }

    // --- Non-phase entry points (not registered as workflow steps; see header map) ---

    public static bool TryInjectLtxAudio(
        WorkflowGenerator g,
        WGNodeData audio,
        bool matchVideoLengthToAudio = true,
        IReadOnlyList<(double Start, double End)> preserveWindows = null)
    {
        return BuildPipeline(g).LtxManager.TryInjectAudio(audio, matchVideoLengthToAudio, preserveWindows);
    }

    internal static RootVideoStageResizer GetRootVideoStageResizer(WorkflowGenerator g) =>
        BuildPipeline(g).Resizer;

    private readonly record struct Pipeline(
        StageRefStore StageRefStore,
        RootVideoStageHandoff Handoff,
        RootVideoStageResizer Resizer,
        StageGuideMediaHelper GuideMediaHelper,
        Base2EditPublishedStageRefs Base2Edit,
        LtxManager LtxManager);

    private static Pipeline BuildPipeline(WorkflowGenerator g)
    {
        StageRefStore stageRefStore = new(g);
        RootVideoStageHandoff handoff = new(g, stageRefStore);
        RootVideoStageResizer resizer = new(g, handoff);
        StageGuideMediaHelper guideMediaHelper = new(g);
        Base2EditPublishedStageRefs base2Edit = new(g);
        LtxManager ltxManager = new(g, resizer, guideMediaHelper, base2Edit);
        return new Pipeline(stageRefStore, handoff, resizer, guideMediaHelper, base2Edit, ltxManager);
    }

    private static bool IsExtensionActive(WorkflowGenerator g) => VideoStagesPromptSection.IsActive(g);

    private static bool RequireSupportedActiveExecution(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return false;
        }
        _ = g.RequireLtxVideoExecutionPlanContext();
        return true;
    }

    private static bool HasConfiguredStages(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return false;
        }

        return g.GetVideoStagesSpec().Clips.Any(c => c.Stages.Count > 0);
    }
}
