using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

public class RootExecutionPolicyTests
{
    [Fact]
    public void Ordinary_text_to_video_handoff_replaces_root_and_suppresses_its_native_audio()
    {
        VideoExecutionPlan plan = Compile(
            isTextToVideo: true,
            new RootEnvironment(HostRootKind.TextToVideoRoot, CanHandoffHostCore: true),
            GeneratedClip(0));
        RootExecutionPolicy policy = For(plan);
        ClipPlan clip = Assert.Single(plan.Clips);
        StagePlan stage = Assert.Single(clip.Stages);

        Assert.True(policy.InterceptsHostCore);
        Assert.True(policy.UsesStageHandoff);
        Assert.True(policy.ReplacesTextToVideoRootStage(stage, clip));
        Assert.True(policy.SuppressesNativeAudioForStage(stage, clip));
    }

    [Fact]
    public void Image_to_video_generated_clip_uses_handoff_without_text_root_replacement()
    {
        VideoExecutionPlan plan = Compile(
            isTextToVideo: false,
            new RootEnvironment(HostRootKind.ImageToVideo, CanHandoffHostCore: true),
            GeneratedClip(0));
        RootExecutionPolicy policy = For(plan);
        ClipPlan clip = Assert.Single(plan.Clips);
        StagePlan stage = Assert.Single(clip.Stages);

        Assert.True(policy.UsesStageHandoff);
        Assert.False(policy.ReplacesTextToVideoRootStage(stage, clip));
        Assert.False(policy.SuppressesNativeAudioForStage(stage, clip));
    }

    [Fact]
    public void Sourced_lead_text_to_video_drops_root_donor_without_suppressing_source_audio()
    {
        VideoExecutionPlan plan = Compile(
            isTextToVideo: true,
            new RootEnvironment(HostRootKind.TextToVideoRoot, CanHandoffHostCore: true),
            SourcedClip(0),
            GeneratedClip(1));
        RootExecutionPolicy policy = For(plan);
        ClipPlan generated = plan.Clips[1];
        StagePlan stage = Assert.Single(generated.Stages);

        Assert.True(policy.DropsTextToVideoRootDonor);
        Assert.False(policy.UsesStageHandoff);
        Assert.True(policy.ReplacesTextToVideoRootStage(stage, generated));
        Assert.False(policy.SuppressesNativeAudioForStage(stage, generated));
    }

    [Fact]
    public void Sourced_image_to_video_clip_bypasses_root_stage_handoff()
    {
        VideoExecutionPlan plan = Compile(
            isTextToVideo: false,
            new RootEnvironment(HostRootKind.ImageToVideo, CanHandoffHostCore: true),
            SourcedClip(0));
        RootExecutionPolicy policy = For(plan);
        ClipPlan clip = Assert.Single(plan.Clips);

        Assert.True(policy.FirstClipIsSourced);
        Assert.False(policy.UsesStageHandoff);
        Assert.False(policy.ConformsSurvivingRootMedia);
        Assert.False(policy.ReplacesTextToVideoRootStage(null, clip));
    }

    private static RootExecutionPolicy For(VideoExecutionPlan plan) => new(plan);

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

    private static ClipSpec SourcedClip(int id) => GeneratedClip(id) with
    {
        SourceVideo = new SourceVideoSpec("data:video/mp4;base64,QUJD", "source.mp4", 0)
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
