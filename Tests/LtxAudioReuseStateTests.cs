using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Ltx2;
using VideoStages.Authoring;
using VideoStages.Planning;
using Xunit;

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
    public void Stage1_RemembersAudioPathWithoutReplacingAttachedAudio()
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

        LtxAudioReuseState.PrepareReusableAudio(g, clipContext, plannedClip.Stages[1]);

        Assert.True(clipContext.AudioReuse.TryGetPath(out JArray remembered));
        Assert.Equal("200", $"{remembered[0]}");
        Assert.Equal(0L, (long)remembered[1]);

        Assert.Same(mediaBefore, g.CurrentMedia);
        Assert.Same(attachedBefore, g.CurrentMedia.AttachedAudio);
    }

    [Fact]
    public void Stage0_ClearsCarriedOverRememberedPath()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator g = BuildGenerator();
        g.CurrentMedia = MakeVideoMedia(g);

        ClipSpec clip = MakeReusableAudioClip();
        VideoExecutionPlan plan = Plan(clip);
        ClipPlan plannedClip = plan.Clips[0];
        ClipContext clipContext = new(plan, plannedClip, sourceMedia: null, sourceVae: null);
        clipContext.AudioReuse.Remember(new JArray("999", 0));

        LtxAudioReuseState.PrepareReusableAudio(g, clipContext, plannedClip.Stages[0]);

        Assert.False(clipContext.AudioReuse.TryGetPath(out JArray _));
    }

    [Fact]
    public void Stage2_AppliesRememberedPathToAttachedAudio()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator g = BuildGenerator();
        g.CurrentMedia = MakeVideoMedia(g, new JArray("400", 0));

        ClipSpec clip = MakeReusableAudioClip();
        VideoExecutionPlan plan = Plan(clip);
        ClipPlan plannedClip = plan.Clips[0];
        ClipContext clipContext = new(plan, plannedClip, sourceMedia: null, sourceVae: null);
        clipContext.AudioReuse.Remember(new JArray("200", 0));

        LtxAudioReuseState.PrepareReusableAudio(g, clipContext, plannedClip.Stages[2]);

        Assert.NotNull(g.CurrentMedia.AttachedAudio);
        JArray applied = (JArray)g.CurrentMedia.AttachedAudio.Path;
        Assert.Equal("200", $"{applied[0]}");
        Assert.Equal(0L, (long)applied[1]);
        Assert.Equal(WGNodeData.DT_LATENT_AUDIO, g.CurrentMedia.AttachedAudio.DataType);
    }

    [Fact]
    public void Stage1_CompletesCaptureFromPostVideoChainThroughSingleStateOwner()
    {
        using SwarmUiTestContext _ = new();
        ClipSpec clip = MakeReusableAudioClip();
        VideoExecutionPlan plan = Plan(clip);
        ClipPlan plannedClip = plan.Clips[0];
        Ltx2ClipAudioReuseState audioReuse = new();
        JArray capturedAudioPath = new("captured", 1);

        LtxAudioReuseState.CompletePostVideoChainCapture(
            audioReuse,
            plannedClip.Stages[1],
            capturedAudioPath);

        Assert.True(audioReuse.TryGetPath(out JArray remembered));
        Assert.Equal(new JArray("captured", 1), remembered);
        Assert.NotSame(capturedAudioPath, remembered);
    }
}
