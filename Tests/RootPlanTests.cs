using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class RootPlanTests
{
    [Fact]
    public void Empty_plan_has_no_root_execution_actions()
    {
        RootPlan root = RootPlanCompiler.Compile(
            new RootEnvironment(
                HostRootKind.TextToVideoRoot,
                CanHandoffHostCore: true),
            []);

        Assert.False(root.DiscardsRoot);
        Assert.False(root.UsesGeneratedClipDonor);
        Assert.False(root.InterceptsHostCore);
        Assert.False(root.UsesStageHandoff);
        Assert.False(root.DropsTextToVideoRootDonor);
        Assert.False(root.DiscardsTextToVideoRoot);
    }

    [Fact]
    public void Ordinary_text_to_video_compiles_handoff_and_root_replacement()
    {
        VideoExecutionPlan plan = Compile(
            isTextToVideo: true,
            new RootEnvironment(HostRootKind.TextToVideoRoot, CanHandoffHostCore: true),
            GeneratedClip(0));
        RootPlan root = plan.Root;
        ClipPlan clip = Assert.Single(plan.Clips);
        StagePlan stage = Assert.Single(clip.Stages);

        Assert.True(root.InterceptsHostCore);
        Assert.True(root.UsesStageHandoff);
        Assert.True(root.ReplacesTextToVideoRootStage(stage, clip));
    }

    [Fact]
    public void Image_to_video_generated_clip_uses_handoff_without_text_root_replacement()
    {
        VideoExecutionPlan plan = Compile(
            isTextToVideo: false,
            new RootEnvironment(HostRootKind.ImageToVideo, CanHandoffHostCore: true),
            GeneratedClip(0));
        RootPlan root = plan.Root;
        ClipPlan clip = Assert.Single(plan.Clips);
        StagePlan stage = Assert.Single(clip.Stages);

        Assert.True(root.UsesStageHandoff);
        Assert.False(root.ReplacesTextToVideoRootStage(stage, clip));
    }

    [Fact]
    public void InitVideo_lead_text_to_video_drops_donor_without_stage_handoff()
    {
        VideoExecutionPlan plan = Compile(
            isTextToVideo: true,
            new RootEnvironment(HostRootKind.TextToVideoRoot, CanHandoffHostCore: true),
            InitVideoClip(0),
            GeneratedClip(1));
        RootPlan root = plan.Root;
        ClipPlan generated = plan.Clips[1];
        StagePlan stage = Assert.Single(generated.Stages);

        Assert.True(root.DropsTextToVideoRootDonor);
        Assert.False(root.UsesStageHandoff);
        Assert.True(root.ReplacesTextToVideoRootStage(stage, generated));
    }

    [Fact]
    public void InitVideo_image_to_video_clip_bypasses_root_stage_handoff()
    {
        VideoExecutionPlan plan = Compile(
            isTextToVideo: false,
            new RootEnvironment(HostRootKind.ImageToVideo, CanHandoffHostCore: true),
            InitVideoClip(0));
        RootPlan root = plan.Root;

        Assert.False(root.UsesStageHandoff);
        Assert.False(root.UsesGeneratedClipDonor);
    }

    private static VideoExecutionPlan Compile(
        bool isTextToVideo,
        RootEnvironment root,
        params ClipSpec[] clips) =>
        TestPlanCompiler.Compile(
            new VideoStagesSpec(512, 512, 24, isTextToVideo, clips),
            root);

    private static ClipSpec GeneratedClip(int id) => new(
        id,
        49,
        Constants.AudioSourceNative,
        [],
        false,
        false,
        false,
        false,
        null,
        [],
        [Stage(id)]);

    private static ClipSpec InitVideoClip(int id) => GeneratedClip(id) with
    {
        InitVideo = new InitVideoSpec("data:video/mp4;base64,QUJD", "source.mp4", 0)
    };

    private static StageSpec Stage(int id) => new(
        id,
        1,
        1,
        "pixel-lanczos",
        "ltx-2",
        8,
        1,
        "euler",
        "normal",
        "Generated");
}
