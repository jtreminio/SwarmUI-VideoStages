using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Ltx2;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// A non-cut boundary is all-or-nothing: every prerequisite the previous clip's output fails to
/// supply must take the whole boundary down to a cut, so timeline assembly never trims an overlap
/// whose conditioning was never applied.
/// </summary>
[Collection("VideoStagesTests")]
public class BoundaryHandoffResolverTests
{
    private const string VideoNodeId = "10";
    private const string AudioNodeId = "40";
    private const int ClipFrames = 49;

    private static WorkflowGenerator NewGenerator()
    {
        // Side-effect: registers the VideoStages node types used by the carry builder.
        _ = WorkflowTestHarness.VideoStagesSteps();
        JObject workflow = [];
        WorkflowGenerator g = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            Workflow = workflow,
        };
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            bridge.AddStub("UnitTest_ClipVideo", VideoNodeId).WithOutputs(WGNodeData.DT_IMAGE);
            bridge.AddStub("UnitTest_ClipAudio", AudioNodeId).WithOutputs(WGNodeData.DT_AUDIO);
        }
        return g;
    }

    private static VideoExecutionPlan CrossfadeCarryPlan()
    {
        VideoStagesSpec spec = new(512, 512, 24, false,
        [
            GeneratedClip(0) with
            {
                BoundaryOut = Constants.BoundaryOutCrossfade,
                BoundaryOutOverlap = 8,
                BoundaryOutCarryAudio = true,
            },
            GeneratedClip(1),
        ]);
        return TestPlanCompiler.Compile(spec);
    }

    private static ClipSpec GeneratedClip(int id) =>
        new(id, ClipFrames, Constants.AudioSourceNative, [], false, false, false, false, null, [],
        [
            new StageSpec(10 + id, 1, 1, "pixel-lanczos", "ltx-2", 12, 4.5, "euler", "normal",
                "Generated", ClipStageIndex: 0, ClipStageRawIndex: 0),
        ]);

    private static WGNodeData PreviousOutput(
        WorkflowGenerator g,
        int? frames = ClipFrames,
        JToken fps = null,
        string audioNodeId = AudioNodeId)
    {
        WGNodeData media = new(new JArray(VideoNodeId, 0), g, WGNodeData.DT_VIDEO, null)
        {
            Width = 512,
            Height = 512,
            Frames = frames,
            FPS = fps ?? new JValue(24),
        };
        if (audioNodeId is not null)
        {
            media.AttachedAudio = new WGNodeData(
                new JArray(audioNodeId, 0), g, WGNodeData.DT_AUDIO, null);
        }
        return media;
    }

    private static (BoundaryHandoffResolver Resolver, TimelineAssemblySession Assembly, ClipContext Context)
        Arrange(WorkflowGenerator g, VideoExecutionPlan plan) =>
        (new BoundaryHandoffResolver(
                new ContinuityGuideBuilder(g),
                new LtxBoundaryAudioCarryBuilder(g)),
            new TimelineAssemblySession(g, new MultiClipParallelMerger(g), plan),
            new ClipContext(plan, plan.Clips[1], null, null));

    public static TheoryData<string> MissingCarryPrerequisites() =>
        ["no-previous-output", "no-attached-audio", "unresolvable-audio", "unknown-frames",
            "non-integer-fps", "window-longer-than-clip"];

    [Theory]
    [MemberData(nameof(MissingCarryPrerequisites))]
    public void Missing_audio_carry_prerequisite_degrades_the_whole_boundary_to_a_cut(string prerequisite)
    {
        WorkflowGenerator g = NewGenerator();
        VideoExecutionPlan plan = CrossfadeCarryPlan();
        (BoundaryHandoffResolver resolver, TimelineAssemblySession assembly, ClipContext context) =
            Arrange(g, plan);
        Assert.True(assembly.TryGetAudioCarryWindow(0, out _));

        WGNodeData previousOutput = prerequisite switch
        {
            "no-previous-output" => null,
            "no-attached-audio" => PreviousOutput(g, audioNodeId: null),
            "unresolvable-audio" => PreviousOutput(g, audioNodeId: "9999"),
            "unknown-frames" => PreviousOutput(g, frames: null),
            "non-integer-fps" => PreviousOutput(g, fps: new JValue(23.976)),
            "window-longer-than-clip" => PreviousOutput(g, frames: 4),
            _ => throw new ArgumentOutOfRangeException(nameof(prerequisite)),
        };

        LtxBoundaryAudioCarry carry = resolver.Resolve(
            assembly,
            plan.Clips[0],
            previousOutput,
            plan.Clips[1],
            nextClipHasInitVideo: false,
            context);

        Assert.Null(carry);
        Assert.Null(context.ContinuityFrame);
        Assert.False(assembly.TryGetAudioCarryWindow(0, out _));
        Assert.False(assembly.TryGetContinueInput(0, out _, out _));
        string warning = Assert.Single(
            Assert.IsType<List<string>>(g.UserInput.ExtraMeta["parser_warnings"]));
        Assert.Contains("cannot carry audio", warning, StringComparison.Ordinal);
        Assert.Contains("treating the boundary as a cut", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolvable_audio_carry_keeps_the_overlapped_boundary()
    {
        WorkflowGenerator g = NewGenerator();
        VideoExecutionPlan plan = CrossfadeCarryPlan();
        (BoundaryHandoffResolver resolver, TimelineAssemblySession assembly, ClipContext context) =
            Arrange(g, plan);

        LtxBoundaryAudioCarry carry = resolver.Resolve(
            assembly,
            plan.Clips[0],
            PreviousOutput(g),
            plan.Clips[1],
            nextClipHasInitVideo: false,
            context);

        Assert.NotNull(carry);
        Assert.True(assembly.TryGetAudioCarryWindow(0, out int window));
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        TrimAudioDurationNode tail = Assert.Single(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal(window / 24.0, tail.Duration.LiteralAsDouble()!.Value, precision: 6);
    }
}
