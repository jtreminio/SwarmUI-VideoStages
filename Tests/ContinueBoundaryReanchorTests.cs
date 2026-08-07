using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Authoring;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// The re-anchor predicate over a hand-built plan. It covers stage shapes the generated-workflow
/// tests in <see cref="Ltx2BoundaryContractTests"/> only reach indirectly — a retake past the clip
/// head, and a stage that regenerates nothing — so it stays a direct unit test.
/// </summary>
public partial class StageFlowTests
{
    private static ClipSpec ReanchorClipSpec(int id, params StageSpec[] stages) =>
        new(id, 49, MediaSource.Native, [], false, false, false, false, null, [], stages);

    private static StageSpec ReanchorStageSpec(int stageIndex, string model) =>
        new(10 + stageIndex, 1, 1, "pixel-lanczos", model, 12, 4.5, "euler", "normal", "Generated",
            ClipStageIndex: stageIndex, ClipStageRawIndex: stageIndex);

    [Fact]
    public void Only_stages_that_regenerate_the_head_reanchor_the_boundary_tail()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        string model = models.VideoModel.Name;

        TimelineSpec spec = new(512, 512, 24, false,
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
            ContinuityTail = new WGNodeData(new JArray("1", 0), null, WGNodeData.DT_IMAGE, null),
        };

        Assert.True(context.ReanchorsContinuityTail(stages[0]));
        Assert.True(context.ReanchorsContinuityTail(stages[1]));
        Assert.False(context.ReanchorsContinuityTail(stages[2]));
        Assert.False(context.ReanchorsContinuityTail(stages[3]));
        Assert.True(context.ReanchorsContinuityTail(stages[4]));

        context.ContinuityTail = null;
        Assert.All(stages, stage => Assert.False(context.ReanchorsContinuityTail(stage)));
    }
}
