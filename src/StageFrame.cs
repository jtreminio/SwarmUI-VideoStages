using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.LTX2;

namespace VideoStages;

internal sealed class StageFrame
{
    public StageFrame(
        JsonParser.StageSpec stage,
        int sectionId,
        ClipContext clipContext,
        JArray priorOutputPath,
        bool replacesTextToVideoRoot,
        LtxPostVideoChainCapture postVideoChain,
        WGNodeData sourceMedia,
        StageGenerationPlan plan,
        bool parallelMultiClip)
    {
        Stage = stage;
        SectionId = sectionId;
        ClipContext = clipContext;
        PriorOutputPath = priorOutputPath;
        ReplacesTextToVideoRoot = replacesTextToVideoRoot;
        PostVideoChain = postVideoChain;
        SourceMedia = sourceMedia;
        Plan = plan;
        ParallelMultiClip = parallelMultiClip;
    }

    public JsonParser.StageSpec Stage { get; }
    public int SectionId { get; }
    public ClipContext ClipContext { get; }
    public JArray PriorOutputPath { get; }
    public bool ReplacesTextToVideoRoot { get; }
    public LtxPostVideoChainCapture PostVideoChain { get; }
    public WGNodeData SourceMedia { get; }
    public StageGenerationPlan Plan { get; }
    public bool ParallelMultiClip { get; }

    public bool NeedsCropGuidesAfterSampler { get; set; }
}
