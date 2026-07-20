using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.LTX2;

namespace VideoStages;

internal sealed class StageFrame
{
    public StageFrame(
        StageSpec stage,
        int sectionId,
        ClipContext clipContext,
        JArray priorOutputPath,
        bool replacesTextToVideoRoot,
        LtxPostVideoChainCapture postVideoChain,
        WGNodeData sourceMedia,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        bool parallelMultiClip)
    {
        Stage = stage;
        SectionId = sectionId;
        ClipContext = clipContext;
        PriorOutputPath = priorOutputPath;
        ReplacesTextToVideoRoot = replacesTextToVideoRoot;
        PostVideoChain = postVideoChain;
        SourceMedia = sourceMedia;
        GenInfo = genInfo;
        ParallelMultiClip = parallelMultiClip;
    }

    public StageSpec Stage { get; }
    public int SectionId { get; }
    public ClipContext ClipContext { get; }
    public JArray PriorOutputPath { get; }
    public bool ReplacesTextToVideoRoot { get; }
    public LtxPostVideoChainCapture PostVideoChain { get; }
    public WGNodeData SourceMedia { get; }
    public WorkflowGenerator.ImageToVideoGenInfo GenInfo { get; }
    public bool ParallelMultiClip { get; }

    public bool NeedsCropGuidesAfterSampler { get; set; }
}
