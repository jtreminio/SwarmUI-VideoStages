using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.LTX2;

internal sealed class LtxManager
{
    private readonly WorkflowGenerator g;
    private readonly LtxAudioInjector audioInjector;
    private readonly LtxAudioMaskResizer audioMaskResizer;
    private readonly LtxStageOrchestrator stageOrchestrator;

    public LtxManager(
        WorkflowGenerator g,
        RootVideoStageResizer rootVideoStageResizer,
        StageGuideMediaHelper stageGuideMediaHelper,
        Base2EditPublishedStageRefs base2EditPublishedStageRefs)
    {
        this.g = g;
        audioMaskResizer = new LtxAudioMaskResizer(g, rootVideoStageResizer);
        audioInjector = new LtxAudioInjector(g, rootVideoStageResizer);
        LtxStageExecutor stageExecutor = new(
            g,
            rootVideoStageResizer);
        LtxClipRefResolver clipRefResolver = new(
            g,
            stageGuideMediaHelper,
            base2EditPublishedStageRefs);
        stageOrchestrator = new LtxStageOrchestrator(
            g,
            stageExecutor,
            stageGuideMediaHelper,
            clipRefResolver);
    }

    public bool TryInjectAudio(
        WGNodeData audio,
        bool matchVideoLengthToAudio = true,
        IReadOnlyList<(double Start, double End)> preserveWindows = null) =>
        audioInjector.TryInject(audio, matchVideoLengthToAudio, preserveWindows);

    public WGNodeData TryBuildPreserveWindowedAudioLatent(
        WGNodeData audio,
        IReadOnlyList<(double Start, double End)> preserveWindows,
        int stableIdSlot) =>
        audioInjector.TryBuildPreserveWindowedAudioLatent(audio, preserveWindows, stableIdSlot);

    public bool TryApplyControlNetFrameCount(string controlNetSource)
    {
        if (!g.IsLTXV2())
        {
            return false;
        }
        if (!new ControlNetCapture(g).TryCreateCapturedControlImageFrameCount(
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

    public void PrepareReusableAudio(ClipContext clipContext, StagePlan stage) =>
        LtxAudioReuseState.PrepareReusableAudio(g, clipContext, stage);

    public LtxPostVideoChainCapture TryCapturePostVideoChain(ClipContext clipContext, StagePlan stage) =>
        LtxPostVideoChainCapture.TryCapture(g, clipContext, stage);

    public void ApplyPostVideoChainCaptureIfPresent(
        ref WGNodeData referenceMedia,
        ref WGNodeData referenceVae)
    {
        LtxPostVideoChainCapture postVideoChain = LtxPostVideoChainCapture.TryCapture(g);
        if (postVideoChain is null)
        {
            return;
        }
        referenceMedia = postVideoChain.CreateStageInput();
        referenceVae = postVideoChain.CreateStageInputVae();
    }

    public void RunStage(
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        LtxPostVideoChainCapture postVideoChain) =>
        stageOrchestrator.RunLtxPath(
            guideReference,
            refStore,
            genInfo,
            stageFrame,
            sourceMedia,
            priorOutputPath,
            postVideoChain);
}
