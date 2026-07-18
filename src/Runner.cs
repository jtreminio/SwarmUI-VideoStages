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
// 7  11.5   RunConfiguredStages                           StageRefStore.Base/Refiner,                    writes StageRefStore.Generated (intra-phase fallback only)
//                                                         videostages.controlnet.fullimage.{i}
//
// Non-phase entry points (NOT registered as workflow steps — called from outside the pipeline):
//   TryInjectLtxAudio         Tests/AudioInjectionTests — unit-level audio injection coverage.
//   GetRootVideoStageResizer  RootVideoStageResizer.RegisterHandlers — static AltImageToVideo handlers.
public static class Runner
{
    public static void CaptureCoreVideoControlNetPreprocessors(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return;
        }

        new ControlNetApplicator(g).CaptureCoreVideoControlNetPreprocessors();
    }

    public static void CaptureBase(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g) || !HasConfiguredStages(g))
        {
            return;
        }

        StageRefStore stageRefStore = new(g);
        stageRefStore.Capture(StageRefStore.StageKind.Base);
    }

    public static void CaptureRefiner(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g) || !HasConfiguredStages(g))
        {
            return;
        }

        StageRefStore stageRefStore = new(g);
        stageRefStore.Capture(StageRefStore.StageKind.Refiner);
    }

    public static void CapturePreCoreVideoMedia(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return;
        }

        StageRefStore stageRefStore = new(g);
        RootVideoStageHandoff rootVideoStageHandoff = new(g, stageRefStore);
        rootVideoStageHandoff.CapturePreCoreVideoMedia();
    }

    public static void DropCoreImageToVideoOutput(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return;
        }

        StageRefStore stageRefStore = new(g);
        RootVideoStageHandoff rootVideoStageHandoff = new(g, stageRefStore);
        rootVideoStageHandoff.DropCoreImageToVideoOutput();
    }

    public static void ApplyRootAudioMaskDimensionsAfterNativeVideo(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return;
        }

        StageRefStore stageRefStore = new(g);
        RootVideoStageHandoff rootVideoStageHandoff = new(g, stageRefStore);
        RootVideoStageResizer rootVideoStageResizer = new(g, rootVideoStageHandoff);
        StageGuideMediaHelper stageGuideMediaHelper = new(g);
        Base2EditPublishedStageRefs base2EditPublishedStageRefs = new(g);
        LtxManager ltxManager = new(
            g,
            rootVideoStageHandoff,
            rootVideoStageResizer,
            stageGuideMediaHelper,
            base2EditPublishedStageRefs);
        ltxManager.ApplyRootAudioMaskDimensionsAfterNativeVideo();
    }

    public static void RunConfiguredStages(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return;
        }

        StageRefStore stageRefStore = new(g);
        RootVideoStageHandoff rootVideoStageHandoff = new(g, stageRefStore);
        RootVideoStageResizer rootVideoStageResizer = new(g, rootVideoStageHandoff);
        StageGuideMediaHelper stageGuideMediaHelper = new(g);
        AudioHandler audioHandler = new(g);
        Base2EditPublishedStageRefs base2EditPublishedStageRefs = new(g);
        MultiClipParallelMerger multiClipParallelMerger = new(g);
        LtxManager ltxManager = new(
            g,
            rootVideoStageHandoff,
            rootVideoStageResizer,
            stageGuideMediaHelper,
            base2EditPublishedStageRefs);
        StageRunner stageRunner = new(g, stageGuideMediaHelper, ltxManager);
        StageSequenceRunner stageSequenceRunner = new(
            g,
            stageRefStore,
            stageRunner,
            base2EditPublishedStageRefs,
            rootVideoStageHandoff,
            rootVideoStageResizer,
            multiClipParallelMerger,
            ltxManager);
        VideoStagesCoordinator coordinator = new(
            g,
            rootVideoStageHandoff,
            stageSequenceRunner,
            audioHandler,
            ltxManager);
        coordinator.RunConfiguredStages();
    }

    // --- Non-phase entry points (not registered as workflow steps; see header map) ---

    public static bool TryInjectLtxAudio(
        WorkflowGenerator g,
        WGNodeData audio,
        bool matchVideoLengthToAudio = true,
        IReadOnlyList<(double Start, double End)> preserveWindows = null)
    {
        StageRefStore stageRefStore = new(g);
        RootVideoStageHandoff rootVideoStageHandoff = new(g, stageRefStore);
        RootVideoStageResizer rootVideoStageResizer = new(g, rootVideoStageHandoff);
        StageGuideMediaHelper stageGuideMediaHelper = new(g);
        Base2EditPublishedStageRefs base2EditPublishedStageRefs = new(g);
        LtxManager ltxManager = new(
            g,
            rootVideoStageHandoff,
            rootVideoStageResizer,
            stageGuideMediaHelper,
            base2EditPublishedStageRefs);
        return ltxManager.TryInjectAudio(audio, matchVideoLengthToAudio, preserveWindows);
    }

    internal static RootVideoStageResizer GetRootVideoStageResizer(WorkflowGenerator g)
    {
        StageRefStore stageRefStore = new(g);
        RootVideoStageHandoff rootVideoStageHandoff = new(g, stageRefStore);
        return new RootVideoStageResizer(g, rootVideoStageHandoff);
    }

    private static bool IsExtensionActive(WorkflowGenerator g) => VideoStagesPromptSection.IsActive(g);

    private static bool HasConfiguredStages(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return false;
        }

        return g.GetVideoStagesSpec().Clips.Any(c => c.Stages.Count > 0);
    }
}
