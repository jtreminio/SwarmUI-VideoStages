using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.LTX2;

namespace VideoStages;

public class Runner
{
    private readonly WorkflowGenerator g;
    private readonly JsonParser jsonParser;
    private readonly RootVideoStageTakeover rootVideoStageTakeover;
    private readonly VideoStagesCoordinator coordinator;
    private readonly StageGuideMediaHelper stageGuideMediaHelper;
    private readonly RootVideoStageResizer rootVideoStageResizer;
    private readonly StageRefStore stageRefStore;
    private readonly AudioStageDetector audioStageDetector;
    private readonly Base2EditPublishedStageRefs base2EditPublishedStageRefs;
    private readonly MultiClipParallelMerger multiClipParallelMerger;
    private readonly LtxManager ltxManager;
    private readonly StageRunner stageRunner;
    private readonly StageSequenceRunner stageSequenceRunner;

    public Runner(WorkflowGenerator g)
    {
        this.g = g;
        jsonParser = new JsonParser(g);
        rootVideoStageTakeover = new RootVideoStageTakeover(g, jsonParser);
        stageGuideMediaHelper = new StageGuideMediaHelper(g);
        rootVideoStageResizer = new RootVideoStageResizer(
            g,
            rootVideoStageTakeover,
            jsonParser);
        stageRefStore = new StageRefStore(g);
        audioStageDetector = new AudioStageDetector(g);
        base2EditPublishedStageRefs = new Base2EditPublishedStageRefs(g);
        multiClipParallelMerger = new MultiClipParallelMerger(g);
        ltxManager = new LtxManager(
            g,
            jsonParser,
            rootVideoStageTakeover,
            rootVideoStageResizer,
            stageGuideMediaHelper,
            base2EditPublishedStageRefs);
        stageRunner = new StageRunner(g, stageGuideMediaHelper, ltxManager, base2EditPublishedStageRefs);
        stageSequenceRunner = new StageSequenceRunner(
            g,
            stageRefStore,
            stageRunner,
            base2EditPublishedStageRefs,
            rootVideoStageTakeover,
            rootVideoStageResizer,
            multiClipParallelMerger,
            ltxManager);
        coordinator = new VideoStagesCoordinator(
            g,
            jsonParser,
            rootVideoStageTakeover,
            stageRefStore,
            stageSequenceRunner,
            audioStageDetector,
            ltxManager);
    }

    internal RootVideoStageResizer RootVideoStageResizer => rootVideoStageResizer;

    public bool TryInjectLtxAudio(AudioStageDetector.Detection detection, bool matchVideoLengthToAudio = true) =>
        ltxManager.TryInjectAudio(detection, matchVideoLengthToAudio);

    public void CaptureCoreVideoControlNetPreprocessors()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        ControlNetApplicator.CaptureCoreVideoControlNetPreprocessors(g);
    }

    public void EnsureRootVideoStageModel()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        rootVideoStageTakeover.EnsureRootVideoStageModel();
    }

    public void CaptureBase()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        coordinator.CaptureBase();
    }

    public void CaptureRefiner()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        coordinator.CaptureRefiner();
    }

    public void SuppressCoreRootVideoStage()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        rootVideoStageTakeover.SuppressCoreRootVideoStage();
    }

    public void RestoreCoreRootVideoStageModel()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        rootVideoStageTakeover.RestoreCoreRootVideoStageModel();
    }

    public void ApplyRootAudioMaskDimensionsAfterNativeVideo()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        ltxManager.ApplyRootAudioMaskDimensionsAfterNativeVideo();
    }

    public void RunConfiguredStages()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        coordinator.RunConfiguredStages();
    }

    private bool IsExtensionActive()
    {
        T2IParamType type = VideoStagesExtension.VideoStagesJson?.Type;
        return type is not null && g.UserInput.TryGetRaw(type, out _);
    }
}
