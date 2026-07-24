using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class StageFrame
{
    public StageFrame(
        StagePlan stage,
        int sectionId,
        ClipContext clipContext,
        JArray priorOutputPath,
        bool replacesTextToVideoRoot,
        LtxPostVideoChainCapture postVideoChain,
        WGNodeData sourceMedia,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageExecutionOptions executionOptions)
    {
        Stage = stage;
        SectionId = sectionId;
        ClipContext = clipContext;
        PriorOutputPath = priorOutputPath;
        ReplacesTextToVideoRoot = replacesTextToVideoRoot;
        PostVideoChain = postVideoChain;
        SourceMedia = sourceMedia;
        GenInfo = genInfo;
        ExecutionOptions = executionOptions;
    }

    public StagePlan Stage { get; }
    public int SectionId { get; }
    public ClipContext ClipContext { get; }
    public JArray PriorOutputPath { get; }
    public bool ReplacesTextToVideoRoot { get; }
    public LtxPostVideoChainCapture PostVideoChain { get; }
    public WGNodeData SourceMedia { get; }
    public WorkflowGenerator.ImageToVideoGenInfo GenInfo { get; }
    public StageExecutionOptions ExecutionOptions { get; }

    public bool NeedsCropGuidesAfterSampler { get; set; }

    /// <summary>Set when an audio-consuming IC-LoRA wrapped this stage's conditioning in
    /// LTXVSetAudioRefTokens. Lets guide cropping restore the unwrapped conditioning paths.</summary>
    public bool AudioReferenceActive { get; set; }

    /// <summary>The conditioning paths before audio-reference tokens were applied. Crop-guides
    /// branches from these so the reference-token wrapper affects sampling, not guide cropping.</summary>
    public JArray AudioReferencePreWrapPosCond { get; set; }

    public JArray AudioReferencePreWrapNegCond { get; set; }
}

internal sealed record StageExecutionOptions(
    bool IsParallelMultiClip,
    bool PublishIntermediateStages)
{
    public bool RequiresDedicatedOutput => IsParallelMultiClip || PublishIntermediateStages;
}
