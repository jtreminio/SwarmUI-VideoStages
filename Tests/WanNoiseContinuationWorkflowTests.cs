using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Authoring;
using VideoStages.Planning;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// The high-noise to low-noise pair, which continues one sampling run across two checkpoints
/// instead of decoding between them.
/// </summary>
[Collection("VideoStagesTests")]
public class WanNoiseContinuationWorkflowTests
{
    /// <summary>
    /// A checkpoint list <see cref="WanWorkflowFixture"/> has no factory for. Both architectures'
    /// support models are installed so the same fixture serves the cross-architecture timelines;
    /// each installer replaces the shared VAE handler, so WAN's VAEs are re-added last.
    /// </summary>
    private sealed class MultiModelFixture : VideoStagesWorkflowFixture
    {
        private MultiModelFixture(IReadOnlyList<string> modelFixturePaths, bool withBaseModel)
            : base(modelFixturePaths, withBaseModel)
        {
        }

        public static MultiModelFixture Create(params string[] modelFixturePaths) =>
            new(modelFixturePaths, withBaseModel: false);

        public static MultiModelFixture CreateWithBaseModel(params string[] modelFixturePaths) =>
            new(modelFixturePaths, withBaseModel: true);

        public override JObject Post(JObject document, Action<JObject> customize = null) =>
            base.Post(document, post =>
            {
                post["clipvisionmodel"] = TestModelFactory.WanClipVisionFileName;
                customize?.Invoke(post);
            });

        protected override void InstallSupportModels()
        {
            TestModelFactory.InstallWanSupportModels();
            TestModelFactory.InstallLtx2SupportModels();
            InstallModel("VAE", CommonModels.Known["wan21-vae"].FileName);
            InstallModel("VAE", CommonModels.Known["wan22-vae"].FileName);
        }

        public override int DefaultSteps => WanWorkflowFixture.Steps;

        public override double DefaultCfgScale => WanWorkflowFixture.CfgScale;

        public override int ExpectedGeneratedFrames => WanWorkflowFixture.GeneratedFrames;
    }

    // ---- high/low noise continuation ----------------------------------------------------

    /// <summary>
    /// A high-noise stage followed by a low-noise one is a single sampling run split in two: the
    /// low stage takes the high sampler's leftover-noise latent directly, with no decode and
    /// re-encode between them. Each half keeps its own model, LoRAs and prompt section — the two
    /// prompts are composed into core's <c>&lt;video&gt;</c>/<c>&lt;videoswap&gt;</c> pair, which
    /// is the only thing that ever populates the video-swap section.
    /// <para>
    /// The split step is <c>floor(steps * (1 - control))</c> of the low stage, pinned as literals.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.35, 5)]
    [InlineData(0.5, 4)]
    [InlineData(0.8, 1)]
    public async Task High_to_low_noise_stages_continue_one_sampling_run_without_a_vae_boundary(
        double lowControl,
        int splitStep)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);
        foreach (string name in new[]
        {
            "UnitTest_Wan_High_Prompt", "UnitTest_Wan_High_Persisted",
            "UnitTest_Wan_Low_Prompt", "UnitTest_Wan_Low_Persisted",
        })
        {
            fixture.InstallModel("LoRA", $"{name}.safetensors");
        }
        JObject document = MakeDocument(MakeClip(
            WanWorkflowFixture.StageWithLoras(
                MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 8, cfgScale: 4),
                WanWorkflowFixture.Lora("UnitTest_Wan_High_Persisted", 0.3)),
            WanWorkflowFixture.StageWithLoras(
                MakeStage(
                    fixture.LowNoiseModel.Name,
                    "PreviousStage",
                    control: lowControl,
                    steps: 8,
                    cfgScale: 6.5,
                    sampler: "dpmpp_2m"),
                WanWorkflowFixture.Lora("UnitTest_Wan_Low_Persisted", 0.7))));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document, post =>
                {
                    post["prompt"] =
                        "global <videoclip[0,0]>high-stage-prompt"
                        + " <lora:UnitTest_Wan_High_Prompt:0.2>"
                        + " <videoclip[0,1]>low-stage-prompt"
                        + " <lora:UnitTest_Wan_Low_Prompt:0.8>";
                    post["outputintermediateimages"] = true;
                }),
                extraSteps:
                [
                    // Values only core's own video-swap pass reads. The continuation borrows core's
                    // IsImageToVideoSwap machinery and drives it from the stages instead, so it
                    // must hand the request's section back exactly as it found it. No POST field
                    // populates this section — <videoswap> composes prompt text into it, not
                    // sampler settings.
                    new(g =>
                    {
                        g.UserInput.Set(
                            T2IParamTypes.Steps, 31, T2IParamInput.SectionID_VideoSwap);
                        g.UserInput.Set(
                            T2IParamTypes.CFGScale, 9, T2IParamInput.SectionID_VideoSwap);
                    }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01),
                ]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode high = StageSampler(bridge, 0);
        SwarmKSamplerNode low = StageSampler(bridge, 1);
        Assert.Equal(4.0, high.Cfg.LiteralAsDouble());
        Assert.Equal("euler", high.SamplerName.LiteralAsString());
        Assert.Equal(0, high.StartAtStep.LiteralAsInt());
        Assert.Equal(splitStep, high.EndAtStep.LiteralAsInt());
        Assert.Equal("enable", high.AddNoise.LiteralAsString());
        Assert.Equal("enable", high.ReturnWithLeftoverNoise.LiteralAsString());
        Assert.Equal(6.5, low.Cfg.LiteralAsDouble());
        Assert.Equal("dpmpp_2m", low.SamplerName.LiteralAsString());
        Assert.Equal(splitStep, low.StartAtStep.LiteralAsInt());
        Assert.Equal(10000, low.EndAtStep.LiteralAsInt());
        Assert.Equal("disable", low.AddNoise.LiteralAsString());
        Assert.Equal("disable", low.ReturnWithLeftoverNoise.LiteralAsString());
        Assert.Same(high, low.LatentImage.Connection?.Node);
        // The re-encode every ordinary two-stage clip carries is exactly what a continuation
        // avoids — Two_stages_on_distinct_checkpoints... is the control that one normally exists.
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());

        (UNETLoaderNode highLoader, string[] highLoras) = ModelBranchOf(high);
        (UNETLoaderNode lowLoader, string[] lowLoras) = ModelBranchOf(low);
        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22I2v14bHighNoiseFixturePath),
            highLoader.UnetName.LiteralAsString());
        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22I2v14bLowNoiseFixturePath),
            lowLoader.UnetName.LiteralAsString());
        Assert.Equal(
            ["UnitTest_Wan_High_Persisted.safetensors", "UnitTest_Wan_High_Prompt.safetensors"],
            highLoras);
        Assert.Equal(
            ["UnitTest_Wan_Low_Persisted.safetensors", "UnitTest_Wan_Low_Prompt.safetensors"],
            lowLoras);

        Assert.Equal(
            "high-stage-prompt",
            ConditioningText(high.Positive.Connection?.Node, negative: false));
        Assert.Equal(
            "low-stage-prompt",
            ConditioningText(low.Positive.Connection?.Node, negative: false));

        VAEDecodeNode highDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => decode.Samples.Connection?.Node == high);
        SwarmSaveAnimationWSNode[] saves = [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Equal(2, saves.Length);
        Assert.Single(saves, save => save.Images.Connection?.Node == highDecode);
        Assert.Same(
            low,
            Assert.IsType<VAEDecodeNode>(live.FinalVideoSave().Images.Connection?.Node)
                .Samples.Connection?.Node);
        Assert.False(generator.IsImageToVideoSwap);
        Assert.Equal(
            31,
            generator.UserInput.GetNullable(
                T2IParamTypes.Steps, T2IParamInput.SectionID_VideoSwap, false));
        Assert.Equal(
            9,
            generator.UserInput.GetNullable(
                T2IParamTypes.CFGScale, T2IParamInput.SectionID_VideoSwap, false));

        live.AssertAllLive(high, low, highLoader, lowLoader);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }

    /// <summary>
    /// A source clip enters the continuation the same way a generated one does: the high stage
    /// encodes the conformed footage and the low stage picks up its latent, so the whole clip
    /// decodes exactly once.
    /// </summary>
    [Fact]
    public async Task An_init_video_high_to_low_pair_shares_one_source_sampling_run()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);
        JObject document = MakeDocument(WanWorkflowFixture.SourceClip(
            MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 8),
            MakeStage(
                fixture.LowNoiseModel.Name, "PreviousStage", control: 0.5, steps: 8)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = WanWorkflowFixture.AssertSourceConformChain(bridge);
        SwarmKSamplerNode high = StageSampler(bridge, 0);
        SwarmKSamplerNode low = StageSampler(bridge, 1);
        // The pair plus core's base image pass.
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.Same(high, low.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, high.LatentImage.Connection?.Node, window.Id));
        IVaeDecode decode = BaseImage(bridge, low);
        Assert.Equal(WanWorkflowFixture.SourceClipFrames, generator.CurrentMedia.Frames);

        live.AssertAllLive(window, high, low, (ComfyNode)decode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The request's global end image reaches the continuation's terminal low stage. Core rebuilds
    /// the full conditioning for its swap pass, so both halves of the one sampling run condition on
    /// the same end frame — consistent, since they denoise a single trajectory. The pair still
    /// shares its latent with no VAE boundary.
    /// </summary>
    [Fact]
    public async Task A_high_to_low_continuation_puts_the_global_end_image_on_the_low_stage()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);
        JObject document = MakeDocument(MakeClip(
            MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 8),
            MakeStage(
                fixture.LowNoiseModel.Name, "PreviousStage", control: 0.5, steps: 8)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            document,
            post => post["videoendimage"] = WanWorkflowFixture.EndImagePayload);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode high = StageSampler(bridge, 0);
        SwarmKSamplerNode low = StageSampler(bridge, 1);
        Assert.Same(high, low.LatentImage.Connection?.Node);
        // Core rebuilds the conditioning for its swap pass; with no per-stage prompt the two are
        // identical, so its post-cleanup collapses them onto one node.
        WanFirstLastFrameToVideoNode terminal = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Same(terminal, low.Positive.Connection?.Node);
        Assert.Same(terminal, high.Positive.Connection?.Node);
        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        SwarmLoadImageB64Node endImage = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal(WanWorkflowFixture.EndImageBase64, endImage.ImageBase64.LiteralAsString());
        Assert.Same(
            endImage,
            Assert.IsType<ImageScaleNode>(terminal.EndImage.Connection?.Node)
                .Image.Connection?.Node);
        // The end slot only: the start stays the host base image, so the continuation still opens
        // from what the request generated rather than from the frame it is aiming at.
        Assert.True(ReachesUpstream(
            bridge, terminal.StartImage.Connection?.Node, fixture.BaseSampler(bridge).Id));
        Assert.False(ReachesUpstream(bridge, terminal.StartImage.Connection?.Node, endImage.Id));
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());

        live.AssertAllLive(endImage, terminal, high, low);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The continuation composes both stages' prompts into one <c>&lt;video&gt;/&lt;videoswap&gt;</c>
    /// pair, which cannot express "one half has text and the other does not". Rather than silently
    /// dropping a prompt, planning abandons the continuation: two ordinary passes joined by a
    /// re-encode, the low stage adding its own noise from its own start step.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_half_blank_prompt_falls_back_to_two_ordinary_stages(bool blankIsNegative)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);
        JObject document = MakeDocument(MakeClip(
            MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 8),
            MakeStage(
                fixture.LowNoiseModel.Name, "PreviousStage", control: 0.5, steps: 8)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document, post =>
                {
                    post["prompt"] = blankIsNegative
                        ? "global <videoclip[0,0]>high-positive <videoclip[0,1]>low-positive"
                        : "global <videoclip[0,0]>high-stage <videoclip[0,1]>";
                    if (!blankIsNegative)
                    {
                        return;
                    }
                    post["negativeprompt"] = "<videoclip[0,0]><videoclip[0,1]>low-negative";
                    post["zeronegative"] = true;
                }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode high = StageSampler(bridge, 0);
        SwarmKSamplerNode low = StageSampler(bridge, 1);
        Assert.Equal(10000, high.EndAtStep.LiteralAsInt());
        Assert.Equal("disable", high.ReturnWithLeftoverNoise.LiteralAsString());
        // control 0.5 over 8 steps.
        Assert.Equal(4, low.StartAtStep.LiteralAsInt());
        Assert.Equal("enable", low.AddNoise.LiteralAsString());
        VAEEncodeNode handoff = Assert.IsType<VAEEncodeNode>(low.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, handoff, high.Id));

        if (blankIsNegative)
        {
            // An empty negative is zeroed rather than encoded; keeping that is the whole reason
            // the fallback exists.
            Assert.Equal(
                "ConditioningZeroOut",
                high.Negative.Connection?.Node?.FindInput("negative")
                    .Connection?.Node?.ClassTypeName);
            Assert.Equal(
                "low-negative",
                ConditioningText(low.Negative.Connection?.Node, negative: true));
        }
        else
        {
            Assert.Equal(
                "high-stage",
                ConditioningText(high.Positive.Connection?.Node, negative: false));
            Assert.True(string.IsNullOrWhiteSpace(
                ConditioningText(low.Positive.Connection?.Node, negative: false)));
        }
        Assert.False(generator.IsImageToVideoSwap);

        live.AssertAllLive(handoff, high, low);
        AssertShippable(bridge, workflow, live);
    }
}
