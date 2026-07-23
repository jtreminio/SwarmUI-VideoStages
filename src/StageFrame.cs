using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.LTX2;
using VideoStages.Planning;

namespace VideoStages;

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

    /// <summary>Set when this stage's conditioning was wrapped in an LTXVSetAudioRefTokens node
    /// (voice-reference clip). Drives the frozen-audio carry in CropGuidesAfterSampler.</summary>
    public bool VoiceRefActive { get; set; }

    /// <summary>The conditioning paths as they were BEFORE the voice-ref wrap. LTXVCropGuides
    /// branches from these (matching the official LipDub graph, where the ref-token wrap feeds only
    /// the sampler's guider and never flows into the crop).</summary>
    public JArray VoiceRefPreWrapPosCond { get; set; }

    public JArray VoiceRefPreWrapNegCond { get; set; }
}

internal sealed record StageExecutionOptions(
    bool IsParallelMultiClip,
    bool PublishIntermediateStages)
{
    public bool RequiresDedicatedOutput => IsParallelMultiClip || PublishIntermediateStages;
}
