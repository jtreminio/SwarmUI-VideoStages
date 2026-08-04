using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class StageFrame
{
    public StageFrame(
        StagePlan stage,
        ClipContext clipContext,
        JArray priorOutputPath,
        bool replacesTextToVideoRoot,
        LtxPostVideoChainCapture postVideoChain,
        WGNodeData sourceMedia,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        bool requiresDedicatedOutput)
    {
        Stage = stage;
        ClipContext = clipContext;
        PriorOutputPath = priorOutputPath;
        ReplacesTextToVideoRoot = replacesTextToVideoRoot;
        PostVideoChain = postVideoChain;
        SourceMedia = sourceMedia;
        GenInfo = genInfo;
        RequiresDedicatedOutput = requiresDedicatedOutput;
    }

    public StagePlan Stage { get; }
    public ClipContext ClipContext { get; }
    public JArray PriorOutputPath { get; }
    public bool ReplacesTextToVideoRoot { get; }
    public LtxPostVideoChainCapture PostVideoChain { get; }
    public WGNodeData SourceMedia { get; }
    public WorkflowGenerator.ImageToVideoGenInfo GenInfo { get; }
    public bool RequiresDedicatedOutput { get; }

    public bool NeedsCropGuidesAfterSampler { get; set; }

    /// <summary>Which of core's nodes this stage took over. Claimed once, before the earliest of
    /// them is built, and read again by each builder as its turn comes.</summary>
    public HostRootClaim Claim { get; set; } = HostRootClaim.None;

    /// <summary>The continue boundary's tail, conformed to this stage's resolution, for a stage past
    /// the clip's first that regenerates the head. The opening stage takes the tail as its primary
    /// guide instead; later stages already have their own input contract, so the anchor is layered on
    /// top of it and leaves every frame past the overlap window exactly as the handoff left it.</summary>
    public WGNodeData ContinuityAnchor { get; set; }

    /// <summary>Set when an audio-consuming IC-LoRA wrapped this stage's conditioning in
    /// LTXVSetAudioRefTokens. Lets guide cropping restore the unwrapped conditioning paths.</summary>
    public bool AudioReferenceActive { get; set; }

    /// <summary>The conditioning paths before audio-reference tokens were applied. Crop-guides
    /// branches from these so the reference-token wrapper affects sampling, not guide cropping.</summary>
    public JArray AudioReferencePreWrapPosCond { get; set; }

    public JArray AudioReferencePreWrapNegCond { get; set; }
}
