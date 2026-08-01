using ComfyTyped.Core;
using ComfyTyped.Generated;
using VideoStages.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// A continue boundary freezes the previous clip's tail as the next clip's opening latent context.
/// Each stage that regenerates the clip head reapplies the tail at that stage's resolution.
/// </summary>
public partial class StageFlowTests
{
    private const int ReanchorTailIndex = ContinueClipFrames - ContinueWindowFrames;

    /// <summary>Creates a 0.6-second clip with 17 aligned frames.</summary>
    private static JObject ReanchorClip(params JObject[] stages)
    {
        JObject clip = MakeClip(stages);
        clip["duration"] = 0.6;
        return clip;
    }

    private static ClipSpec ReanchorClipSpec(int id, params StageSpec[] stages) =>
        new(id, 49, Constants.AudioSourceNative, [], false, false, false, false, null, [], stages);

    private static StageSpec ReanchorStageSpec(int stageIndex, string model) =>
        new(10 + stageIndex, 1, 1, "pixel-lanczos", model, 12, 4.5, "euler", "normal", "Generated",
            ClipStageIndex: stageIndex, ClipStageRawIndex: stageIndex);

    private static JObject RefineStage(string model, double upscale = 1.0) =>
        MakeStage(model, "Generated", control: 0.5, steps: 10, upscale: upscale);

    private static (JObject Workflow, WorkflowGenerator Generator) RunTimeline(
        TestModelBundle models,
        params JObject[] clips) =>
        WorkflowTestHarness.GenerateWithStepsAndState(
            BuildNativeInput(
                models.BaseModel,
                models.VideoModel,
                MakeRootConfig(512, 512, clips).ToString()),
            BuildNativeSteps(attachAudioToCurrentMedia: true));

    /// <summary>
    /// Finds the continuity tail slice sourced from the previous clip output.
    /// </summary>
    private static ImageFromBatchNode ContinuityTailSlice(WorkflowBridge bridge, string sourceNodeId) =>
        Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.BatchIndex.LiteralAsInt() == ReanchorTailIndex
                && node.Length.LiteralAsInt() == ContinueWindowFrames
                && node.Image.Connection?.Node.Id == sourceNodeId
                && bridge.Graph.FindInputsConnectedTo(node.IMAGE).Any(
                    consumer => consumer.Node is ImageScaleNode or LTXVPreprocessNode));

    /// <summary>
    /// Returns image scales from an in-place merge back to the continuity tail, or null if unconnected.
    /// </summary>
    private static List<(int Width, int Height)> TailGuideScales(
        LTXVImgToVideoInplaceNode inplace,
        string tailNodeId)
    {
        List<(int Width, int Height)> scales = [];
        INodeOutput current = inplace.Image.Connection;
        while (current is not null && current.Node.Id != tailNodeId)
        {
            switch (current.Node)
            {
                case ImageScaleNode scale:
                    scales.Add((scale.Width.LiteralAsInt()!.Value, scale.Height.LiteralAsInt()!.Value));
                    current = scale.Image.Connection;
                    break;
                case LTXVPreprocessNode preprocess:
                    current = preprocess.Image.Connection;
                    break;
                default:
                    return null;
            }
        }
        return current is null ? null : scales;
    }

    private static List<LTXVImgToVideoInplaceNode> TailAnchors(
        WorkflowBridge bridge,
        string tailNodeId) =>
        [.. bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>()
            .Where(node => TailGuideScales(node, tailNodeId) is not null)
            .OrderBy(node => int.Parse(node.Id))];

    /// <summary>Finds the sampler decode that feeds a frame slice.</summary>
    private static string ClipOutputDecodeId(WorkflowBridge bridge, SwarmKSamplerNode sampler)
    {
        LTXVSeparateAVLatentNode separate = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSeparateAVLatentNode>(),
            node => node.AvLatent.Connection?.Node.Id == sampler.Id);
        return Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeTiledNode>(),
            node => node.Samples.Connection?.Node.Id == separate.Id
                && bridge.Graph.FindInputsConnectedTo(node.IMAGE).Any(
                    consumer => consumer.Node is ImageFromBatchNode)).Id;
    }

    /// <summary>Returns ordered geometry for slices consumed by timeline merges.</summary>
    private static List<(int Index, int Length)> MergeSlices(WorkflowBridge bridge) =>
        [.. bridge.Graph.NodesOfType<ImageFromBatchNode>()
            .Where(node => bridge.Graph.FindInputsConnectedTo(node.IMAGE).Any(
                consumer => consumer.Node is LTXVLaplacianPyramidBlendNode or BatchImagesNodeNode))
            .Select(node => (node.BatchIndex.LiteralAsInt()!.Value, node.Length.LiteralAsInt()!.Value))
            .OrderBy(slice => slice.Item1)
            .ThenBy(slice => slice.Item2)];

    [Fact]
    public void Continue_boundary_reanchors_the_tail_at_every_stage_that_regenerates_the_head()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Clip 0 exits at 1024 (its second stage upscales); clip 1 opens at 512 and upscales to 1024.
        JObject firstClip = ReanchorClip(
            RefineStage(models.VideoModel.Name),
            RefineStage(models.VideoModel.Name, upscale: 2.0));
        firstClip["boundaryOut"] = Constants.BoundaryOutContinue;
        JObject secondClip = ReanchorClip(
            RefineStage(models.VideoModel.Name),
            RefineStage(models.VideoModel.Name, upscale: 2.0));

        (JObject workflow, WorkflowGenerator g) = RunTimeline(models, firstClip, secondClip);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(4, samplers.Count);
        ImageFromBatchNode tail = ContinuityTailSlice(bridge, ClipOutputDecodeId(bridge, samplers[1]));

        List<LTXVImgToVideoInplaceNode> anchors = TailAnchors(bridge, tail.Id);
        Assert.Equal(2, anchors.Count);
        Assert.All(anchors, anchor => Assert.Equal(1.0, anchor.Strength.LiteralAsDouble()));

        Assert.Equal([(512, 512)], TailGuideScales(anchors[0], tail.Id));

        // The 1024 stage conforms directly from clip 0 without a 512 intermediate.
        List<(int Width, int Height)> finalScales = TailGuideScales(anchors[1], tail.Id);
        Assert.All(finalScales, scale => Assert.Equal((1024, 1024), scale));
        Assert.True(
            ReachesUpstream(bridge, anchors[1], samplers[1].Id),
            "The final stage's anchor must trace back to clip 0's own output.");
        Assert.Equal(2 * ContinueClipFrames - ContinueWindowFrames, g.CurrentMedia.Frames);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Passthrough_stage_adds_no_continuity_anchor()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject firstClip = ReanchorClip(RefineStage(models.VideoModel.Name));
        firstClip["boundaryOut"] = Constants.BoundaryOutContinue;
        // Control 0 with no retake and no latent scaling: the stage regenerates nothing at all.
        JObject secondClip = ReanchorClip(
            RefineStage(models.VideoModel.Name),
            MakeStage(models.VideoModel.Name, "Generated", control: 0.0, steps: 10));

        (JObject workflow, WorkflowGenerator _) = RunTimeline(models, firstClip, secondClip);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        ImageFromBatchNode tail = ContinuityTailSlice(bridge, ClipOutputDecodeId(bridge, samplers[0]));
        Assert.Single(TailAnchors(bridge, tail.Id));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Only_stages_that_regenerate_the_head_reanchor_the_boundary_tail()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        string model = models.VideoModel.Name;

        VideoStagesSpec spec = new(512, 512, 24, false,
        [
            ReanchorClipSpec(0, ReanchorStageSpec(0, model)) with
            {
                BoundaryOut = Constants.BoundaryOutContinue,
                BoundaryOutOverlap = 8,
            },
            ReanchorClipSpec(
                1,
                ReanchorStageSpec(0, model),
                ReanchorStageSpec(1, model),
                // A retake past the head: frames [24, 32) of a 49-frame clip.
                ReanchorStageSpec(2, model) with { RetakeWindow = new RetakeWindowSpec(24, 8, 1.0) },
                // Control 0 with no retake and no latent scaling regenerates nothing.
                ReanchorStageSpec(3, model) with { Control = 0.0 },
                ReanchorStageSpec(4, model)) with
            {
                InitVideo = new("data", "source.mp4", 0),
            },
        ]);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);
        IReadOnlyList<StagePlan> stages = plan.Clips[1].Stages;
        ClipContext context = new(plan, plan.Clips[1], null, null)
        {
            ContinuityFrame = new WGNodeData(new JArray("1", 0), null, WGNodeData.DT_IMAGE, null),
        };

        Assert.True(context.ReanchorsContinuityTail(stages[0]));
        Assert.True(context.ReanchorsContinuityTail(stages[1]));
        Assert.False(context.ReanchorsContinuityTail(stages[2]));
        Assert.False(context.ReanchorsContinuityTail(stages[3]));
        Assert.True(context.ReanchorsContinuityTail(stages[4]));

        context.ContinuityFrame = null;
        Assert.All(stages, stage => Assert.False(context.ReanchorsContinuityTail(stage)));
    }

    [Fact]
    public void Reanchoring_a_later_stage_leaves_the_overlap_frame_count_untouched()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject OneStageBoundary() => ReanchorClip(RefineStage(models.VideoModel.Name));

        JObject singleStageFirst = OneStageBoundary();
        singleStageFirst["boundaryOut"] = Constants.BoundaryOutContinue;
        (JObject singleWorkflow, WorkflowGenerator singleG) =
            RunTimeline(models, singleStageFirst, OneStageBoundary());

        JObject reanchoredFirst = OneStageBoundary();
        reanchoredFirst["boundaryOut"] = Constants.BoundaryOutContinue;
        (JObject reanchoredWorkflow, WorkflowGenerator reanchoredG) = RunTimeline(
            models,
            reanchoredFirst,
            ReanchorClip(
                RefineStage(models.VideoModel.Name),
                RefineStage(models.VideoModel.Name)));

        using WorkflowBridge singleBridge = WorkflowBridge.Create(singleWorkflow);
        using WorkflowBridge reanchoredBridge = WorkflowBridge.Create(reanchoredWorkflow);
        string singleClipZero = ClipOutputDecodeId(singleBridge, SamplerNodesOrdered(singleBridge)[0]);
        string reanchoredClipZero =
            ClipOutputDecodeId(reanchoredBridge, SamplerNodesOrdered(reanchoredBridge)[0]);

        Assert.Single(TailAnchors(singleBridge, ContinuityTailSlice(singleBridge, singleClipZero).Id));
        Assert.Equal(
            2,
            TailAnchors(
                reanchoredBridge,
                ContinuityTailSlice(reanchoredBridge, reanchoredClipZero).Id).Count);
        Assert.Equal(MergeSlices(singleBridge), MergeSlices(reanchoredBridge));
        Assert.Equal(
            ContinueWindowFrames,
            Assert.Single(reanchoredBridge.Graph.NodesOfType<SwarmRampMaskBatchNode>())
                .Frames.LiteralAsInt());
        Assert.Equal(singleG.CurrentMedia.Frames, reanchoredG.CurrentMedia.Frames);
        Assert.Equal(2 * ContinueClipFrames - ContinueWindowFrames, reanchoredG.CurrentMedia.Frames);
    }

    [Fact]
    public void Explicit_first_frame_reference_is_never_anchored_at_any_stage()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject firstClip = ReanchorClip(RefineStage(models.VideoModel.Name));
        firstClip["boundaryOut"] = Constants.BoundaryOutContinue;
        JObject secondClip = MakeClipWithRefs(
            [MakeRef("Base", frame: 1)],
            RefineStage(models.VideoModel.Name),
            RefineStage(models.VideoModel.Name, upscale: 2.0));
        secondClip["duration"] = 0.6;

        (JObject workflow, WorkflowGenerator g) = RunTimeline(models, firstClip, secondClip);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The boundary degraded to a cut, so no stage has a tail to re-anchor.
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.BatchIndex.LiteralAsInt() == ReanchorTailIndex
                && node.Length.LiteralAsInt() == ContinueWindowFrames);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Equal(2 * ContinueClipFrames, g.CurrentMedia.Frames);
    }

    [Fact]
    public void Crossfade_boundary_anchors_nothing_at_any_stage()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject firstClip = ReanchorClip(RefineStage(models.VideoModel.Name));
        firstClip["boundaryOut"] = Constants.BoundaryOutCrossfade;
        JObject secondClip = ReanchorClip(
            RefineStage(models.VideoModel.Name),
            RefineStage(models.VideoModel.Name, upscale: 2.0));

        (JObject workflow, WorkflowGenerator _) = RunTimeline(models, firstClip, secondClip);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        string clipZeroDecode = ClipOutputDecodeId(bridge, SamplerNodesOrdered(bridge)[0]);
        Assert.All(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>(),
            node => Assert.Null(TailGuideScales(node, clipZeroDecode)));
        Assert.NotEmpty(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        AssertWorkflowHasNoCycles(workflow);
    }
}
