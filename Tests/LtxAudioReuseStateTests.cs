using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Authoring;
using VideoStages.Planning;
using Xunit;
using VideoStages.Architectures.Ltx2.Runtime;
using VideoStages.Architectures.Ltx2.Runtime.Audio;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class LtxAudioReuseStateTests
{
    private static WorkflowGenerator BuildGenerator()
    {
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        T2IParamInput input = new(null);
        return new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/",
            Workflow = new JObject()
        };
    }

    private static StageSpec MakeStage(int clipStageIndex) => new(
        Id: clipStageIndex,
        Control: 1.0,
        Upscale: 1.0,
        UpscaleMethod: "pixel-lanczos",
        Model: "model-a",
        Steps: 8,
        CfgScale: 1.0,
        Sampler: "euler",
        Scheduler: "normal",
        ImageReference: "Generated",
        ClipStageIndex: clipStageIndex,
        ClipStageRawIndex: clipStageIndex);

    private static ClipSpec MakeReusableAudioClip() => new(
        Id: 0,
        Frames: null,
        AudioSource: MediaSource.Native,
        IcLoras: null,
        SaveAudioTrack: false,
        ClipLengthFromAudio: false,
        ClipLengthFromControlNet: false,
        ReuseAudio: true,
        UploadedAudio: null,
        FrameRefs: [],
        Stages: [MakeStage(0), MakeStage(1), MakeStage(2)]);

    private static VideoExecutionPlan Plan(ClipSpec clip) =>
        TestPlanCompiler.Compile(new TimelineSpec(512, 512, 24, false, [clip]));

    private static StagePlan StageDoing(ClipPlan clip, StageAudioAction action) =>
        clip.Stages.Single(stage => stage.RequireLtx2Payload().AudioAction == action);

    private static WGNodeData MakeVideoMedia(WorkflowGenerator g, JArray attachedAudioPath = null)
    {
        WGNodeData media = new(new JArray("100", 0), g, WGNodeData.DT_VIDEO, T2IModelClassSorter.CompatLtxv2);
        if (attachedAudioPath is not null)
        {
            media.AttachedAudio = new WGNodeData(
                attachedAudioPath, g, WGNodeData.DT_LATENT_AUDIO, T2IModelClassSorter.CompatLtxv2);
        }
        return media;
    }

    [Fact]
    public void Capture_for_reuse_remembers_the_path_without_replacing_attached_audio()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator g = BuildGenerator();
        g.CurrentMedia = MakeVideoMedia(g, new JArray("200", 0));

        ClipSpec clip = MakeReusableAudioClip();
        VideoExecutionPlan plan = Plan(clip);
        ClipPlan plannedClip = plan.Clips[0];
        ClipContext clipContext = new(plan, plannedClip, sourceMedia: null, sourceVae: null);

        WGNodeData mediaBefore = g.CurrentMedia;
        WGNodeData attachedBefore = g.CurrentMedia.AttachedAudio;

        LtxAudioReuseState.PrepareReusableAudio(
            g, clipContext, StageDoing(plannedClip, StageAudioAction.CaptureForReuse));

        Assert.True(clipContext.AudioReuse.TryGetPath(out JArray remembered));
        Assert.Equal("200", $"{remembered[0]}");
        Assert.Equal(0L, (long)remembered[1]);

        Assert.Same(mediaBefore, g.CurrentMedia);
        Assert.Same(attachedBefore, g.CurrentMedia.AttachedAudio);
    }

    [Fact]
    public void Audio_action_none_clears_a_carried_over_remembered_path()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator g = BuildGenerator();
        g.CurrentMedia = MakeVideoMedia(g);

        ClipSpec clip = MakeReusableAudioClip();
        VideoExecutionPlan plan = Plan(clip);
        ClipPlan plannedClip = plan.Clips[0];
        ClipContext clipContext = new(plan, plannedClip, sourceMedia: null, sourceVae: null);
        clipContext.AudioReuse.Remember(new JArray("999", 0));

        LtxAudioReuseState.PrepareReusableAudio(
            g, clipContext, StageDoing(plannedClip, StageAudioAction.None));

        Assert.False(clipContext.AudioReuse.TryGetPath(out JArray _));
    }

    [Fact]
    public void Reuse_captured_applies_the_remembered_path_as_attached_audio()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator g = BuildGenerator();
        g.CurrentMedia = MakeVideoMedia(g, new JArray("400", 0));

        ClipSpec clip = MakeReusableAudioClip();
        VideoExecutionPlan plan = Plan(clip);
        ClipPlan plannedClip = plan.Clips[0];
        ClipContext clipContext = new(plan, plannedClip, sourceMedia: null, sourceVae: null);
        clipContext.AudioReuse.Remember(new JArray("200", 0));

        LtxAudioReuseState.PrepareReusableAudio(
            g, clipContext, StageDoing(plannedClip, StageAudioAction.ReuseCaptured));

        Assert.NotNull(g.CurrentMedia.AttachedAudio);
        JArray applied = (JArray)g.CurrentMedia.AttachedAudio.Path;
        Assert.Equal("200", $"{applied[0]}");
        Assert.Equal(0L, (long)applied[1]);
        Assert.Equal(WGNodeData.DT_LATENT_AUDIO, g.CurrentMedia.AttachedAudio.DataType);
    }

    [Fact]
    public void Post_video_chain_capture_stores_a_copy_of_the_audio_latent_path()
    {
        using SwarmUiTestContext _ = new();
        ClipSpec clip = MakeReusableAudioClip();
        VideoExecutionPlan plan = Plan(clip);
        ClipPlan plannedClip = plan.Clips[0];
        Ltx2ClipAudioReuseState audioReuse = new();
        JArray capturedAudioPath = new("captured", 1);

        LtxAudioReuseState.CompletePostVideoChainCapture(
            audioReuse,
            StageDoing(plannedClip, StageAudioAction.CaptureForReuse),
            capturedAudioPath);

        Assert.True(audioReuse.TryGetPath(out JArray remembered));
        Assert.Equal(new JArray("captured", 1), remembered);
        Assert.NotSame(capturedAudioPath, remembered);
    }
}
