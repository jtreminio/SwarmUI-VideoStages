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
        RootVideoStageHandoff rootVideoStageHandoff,
        RootVideoStageResizer rootVideoStageResizer,
        StageGuideMediaHelper stageGuideMediaHelper,
        Base2EditPublishedStageRefs base2EditPublishedStageRefs)
    {
        this.g = g;
        audioMaskResizer = new LtxAudioMaskResizer(g, rootVideoStageResizer);
        audioInjector = new LtxAudioInjector(g, jsonParser, rootVideoStageResizer);
        LtxStageExecutor stageExecutor = new(
            g,
            rootVideoStageHandoff,
            rootVideoStageResizer,
            jsonParser);
        LtxClipRefResolver clipRefResolver = new(
            g,
            stageGuideMediaHelper,
            base2EditPublishedStageRefs);
        stageOrchestrator = new LtxStageOrchestrator(
            g,
            stageExecutor,
            rootVideoStageHandoff,
            stageGuideMediaHelper,
            clipRefResolver);
    }

    public bool TryInjectAudio(
        WGNodeData audio,
        bool matchVideoLengthToAudio = true) =>
        audioInjector.TryInject(audio, matchVideoLengthToAudio);

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

    public void PrepareReusableAudio(ClipContext clipContext, JsonParser.StageSpec stage) =>
        LtxAudioReuseState.PrepareReusableAudio(g, clipContext, stage);

    public LtxPostVideoChainCapture TryCapturePostVideoChain(ClipContext clipContext, JsonParser.StageSpec stage) =>
        LtxPostVideoChainCapture.TryCapture(g, clipContext, stage);

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
        StageFrame stageFrame,
        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        LtxPostVideoChainCapture postVideoChain) =>
        stageOrchestrator.TryRunLocalLtxPath(
            stage,
            guideReference,
            refStore,
            genInfo,
            stageFrame,
            applySourceVideoLatent,
            sourceMedia,
            priorOutputPath,
            postVideoChain);
}
