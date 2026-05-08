using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
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
        StageRunner stageRunner = new(g, stageGuideMediaHelper, ltxManager, base2EditPublishedStageRefs);
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
        bool matchVideoLengthToAudio = true)
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
        return ltxManager.TryInjectAudio(audio, matchVideoLengthToAudio);
    }

    internal static RootVideoStageResizer GetRootVideoStageResizer(WorkflowGenerator g)
    {
        StageRefStore stageRefStore = new(g);
        RootVideoStageHandoff rootVideoStageHandoff = new(g, stageRefStore);
        return new RootVideoStageResizer(g, rootVideoStageHandoff);
    }

    private static bool IsExtensionActive(WorkflowGenerator g)
    {
        T2IParamType type = VideoStagesExtension.VideoStagesJson?.Type;
        return type is not null && g.UserInput.TryGetRaw(type, out _);
    }

    private static bool HasConfiguredStages(WorkflowGenerator g)
    {
        T2IParamType type = VideoStagesExtension.VideoStagesJson?.Type;
        if (type is null
            || !g.UserInput.TryGetRaw(type, out object rawValue)
            || rawValue is not string json
            || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = json.AsSpan().Trim();
        if (trimmed.Length == 2 && trimmed[0] == '[' && trimmed[1] == ']')
        {
            return false;
        }

        return g.GetActiveStages().Count > 0;
    }
}
