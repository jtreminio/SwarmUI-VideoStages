using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal sealed class LtxManager
{
    private readonly WorkflowGenerator g;
    private readonly LtxAudioInjector audioInjector;
    private readonly LtxAudioMaskResizer audioMaskResizer;
    private readonly LtxStageOrchestrator stageOrchestrator;

    public LtxManager(
        WorkflowGenerator g,
        JsonParser jsonParser,
        RootVideoStageTakeover rootVideoStageTakeover,
        RootVideoStageResizer rootVideoStageResizer,
        StageGuideMediaHelper stageGuideMediaHelper,
        Base2EditPublishedStageRefs base2EditPublishedStageRefs)
    {
        this.g = g;
        audioMaskResizer = new LtxAudioMaskResizer(g, rootVideoStageResizer);
        audioInjector = new LtxAudioInjector(g, jsonParser, rootVideoStageResizer);
        LtxStageExecutor stageExecutor = new(
            g,
            rootVideoStageTakeover,
            rootVideoStageResizer,
            jsonParser);
        stageOrchestrator = new LtxStageOrchestrator(
            g,
            stageExecutor,
            rootVideoStageTakeover,
            stageGuideMediaHelper,
            base2EditPublishedStageRefs);
    }

    public bool TryInjectAudio(
        AudioStageDetector.Detection detection,
        bool matchVideoLengthToAudio = true) =>
        audioInjector.TryInject(detection, matchVideoLengthToAudio);

    public bool TryApplyControlNetFrameCount(string controlNetSource)
    {
        if (!g.IsLTXV2())
        {
            return false;
        }
        if (!ControlNetApplicator.TryCreateCapturedControlImageFrameCount(
                g,
                controlNetSource,
                out JArray framesConnection))
        {
            return false;
        }
        LtxFrameCountConnector.ApplyToExistingSources(g, framesConnection);
        return true;
    }

    public void ApplyRootAudioMaskDimensionsAfterNativeVideo() =>
        audioMaskResizer.ApplyRootAudioMaskDimensionsAfterNativeVideo();

    public static void ApplyCurrentAudioMaskDimensions(WGNodeData media) =>
        LtxAudioMaskResizer.ApplyCurrentAudioMaskDimensions(media);

    public void PrepareReusableAudio(JsonParser.StageSpec stage) =>
        LtxAudioReuseState.PrepareReusableAudio(g, stage);

    public LtxPostVideoChain TryCapturePostVideoChain(JsonParser.StageSpec stage) =>
        LtxPostVideoChain.TryCapture(g, stage);

    public void ApplyPostVideoChainCaptureIfPresent(
        ref WGNodeData referenceMedia,
        ref WGNodeData referenceVae) =>
        LtxStageRefCapture.ApplyPostVideoChainCaptureIfPresent(
            g,
            ref referenceMedia,
            ref referenceVae);

    public bool TryRunLocalStage(
        JsonParser.StageSpec stage,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        LtxPostVideoChain postVideoChain) =>
        stageOrchestrator.TryRunLocalLtxPath(
            stage,
            guideReference,
            refStore,
            genInfo,
            applySourceVideoLatent,
            sourceMedia,
            priorOutputPath,
            postVideoChain);
}
