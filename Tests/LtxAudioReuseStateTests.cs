using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.LTX2;
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
        Vae: "",
        Steps: 8,
        CfgScale: 1.0,
        Sampler: "euler",
        Scheduler: "normal",
        ImageReference: "Generated",
        ClipStageIndex: clipStageIndex);

    private static ClipSpec MakeReusableAudioClip() => new(
        Id: 0,
        Frames: null,
        AudioSource: Constants.AudioSourceNative,
        ControlNetSource: Constants.ControlNetSourceOne,
        ControlNetLora: "",
        SaveAudioTrack: false,
        ClipLengthFromAudio: false,
        ClipLengthFromControlNet: false,
        ReuseAudio: true,
        UploadedAudio: null,
        ImageRefs: [],
        Stages: [MakeStage(0), MakeStage(1), MakeStage(2)]);

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
        ClipContext clipContext = new(clip, 512, 512, sourceMedia: null, sourceVae: null);

        WGNodeData mediaBefore = g.CurrentMedia;
        WGNodeData attachedBefore = g.CurrentMedia.AttachedAudio;

        LtxAudioReuseState.PrepareReusableAudio(g, clipContext, clip.Stages[1]);

        Assert.True(clipContext.AudioReuse.TryGetPath(out JArray remembered));
        Assert.Equal("200", $"{remembered[0]}");
        Assert.Equal(0L, (long)remembered[1]);

        // Stage 1 captures only — it must not rebuild AttachedAudio (that's stage 2+'s job).
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
        ClipContext clipContext = new(clip, 512, 512, sourceMedia: null, sourceVae: null);
        clipContext.AudioReuse.Remember(new JArray("999", 0));

        LtxAudioReuseState.PrepareReusableAudio(g, clipContext, clip.Stages[0]);

        Assert.False(clipContext.AudioReuse.TryGetPath(out JArray _));
    }

    [Fact]
    public void Stage2_AppliesRememberedPathToAttachedAudio()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator g = BuildGenerator();
        g.CurrentMedia = MakeVideoMedia(g, new JArray("400", 0));

        ClipSpec clip = MakeReusableAudioClip();
        ClipContext clipContext = new(clip, 512, 512, sourceMedia: null, sourceVae: null);
        clipContext.AudioReuse.Remember(new JArray("200", 0));

        LtxAudioReuseState.PrepareReusableAudio(g, clipContext, clip.Stages[2]);

        Assert.NotNull(g.CurrentMedia.AttachedAudio);
        JArray applied = (JArray)g.CurrentMedia.AttachedAudio.Path;
        Assert.Equal("200", $"{applied[0]}");
        Assert.Equal(0L, (long)applied[1]);
        Assert.Equal(WGNodeData.DT_LATENT_AUDIO, g.CurrentMedia.AttachedAudio.DataType);
    }
}
