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

/// <summary>Which Wan passes each authored, prompt-tagged, or stage-scoped LoRA loads on.</summary>
[Collection("VideoStagesTests")]
public class WanLoraWorkflowTests
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

    /// <summary>A clip-level LoRA reaches the stage generated off that clip's source.</summary>
    [Fact]
    public async Task A_source_clip_lora_applies_to_its_generating_stage()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_Wan_Source_Lora.safetensors");
        JObject clip = WanWorkflowFixture.SourceClip(fixture.Stage(control: 0.5, steps: 10));
        clip["loras"] = new JArray(WanWorkflowFixture.Lora("UnitTest_Wan_Source_Lora", 0.6));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = WanWorkflowFixture.AssertSourceConformChain(bridge);
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        AssertModelOnlyLora(
            LoraLoaderNodesOf(bridge),
            "UnitTest_Wan_Source_Lora.safetensors",
            0.6);
        LoraLoaderModelOnlyNode lora = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>());
        Assert.Same(lora, stage.Model.Connection?.Node);
        Assert.IsType<UNETLoaderNode>(lora.Model.Connection?.Node);
        Assert.Null(generator.CurrentMedia.AttachedAudio);

        live.AssertAllLive(window, lora, stage);
        AssertShippable(bridge, workflow, live);
    }

    // ---- LoRAs --------------------------------------------------------------------------

    /// <summary>
    /// WAN's compat class does not target the text encoder, so both prompt-tagged and stage
    /// LoRAs load through <c>LoraLoaderModelOnly</c> with the authored text-encoder weight dropped,
    /// and a zero model weight removes the loader entirely. Prompt LoRAs load first and the stage's
    /// own chain on top, so the sampler reads the end of one chain.
    /// <para>
    /// The confined arm additionally sets a request-level LoRA scoped to <c>BaseOnly</c>: it must
    /// not reach the video sampler, and it must still be on the request afterwards.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, false)]
    [InlineData(WanWorkflowFixture.Wan22Ti2v5bFixturePath, false)]
    [InlineData(WanWorkflowFixture.Wan22Ti2v5bFixturePath, true)]
    public async Task Prompt_and_stage_loras_load_model_only_and_compose_in_order(
        string modelFixturePath,
        bool textEntryWithConfinedHostLora)
    {
        using WanWorkflowFixture fixture = textEntryWithConfinedHostLora
            ? WanWorkflowFixture.Create(modelFixturePath)
            : WanWorkflowFixture.CreateWithBaseModel(modelFixturePath);
        foreach (string name in new[]
        {
            "UnitTest_Wan_Prompt", "UnitTest_Wan_Persisted",
            "UnitTest_Wan_PromptZero", "UnitTest_Wan_PersistedZero",
            "UnitTest_Wan_Base_Confined",
        })
        {
            fixture.InstallModel("LoRA", $"{name}.safetensors");
        }
        JObject stage = WanWorkflowFixture.StageWithLoras(
            fixture.Stage(steps: 10),
            WanWorkflowFixture.Lora("UnitTest_Wan_Persisted", 0.6, textEncoderWeight: 0.7),
            WanWorkflowFixture.Lora("UnitTest_Wan_PersistedZero", 0, textEncoderWeight: 0.9));
        void Customize(JObject post)
        {
            post["prompt"] =
                "global <videoclip[0,0]><lora:UnitTest_Wan_Prompt:0.4:0.8>"
                + "<lora:UnitTest_Wan_PromptZero:0:0.9>";
            if (!textEntryWithConfinedHostLora)
            {
                return;
            }
            post["loras"] = "UnitTest_Wan_Base_Confined";
            post["loraweights"] = "0.95";
            post["loratencweights"] = "0.85";
            post["lorasectionconfinement"] = $"{T2IParamInput.SectionID_BaseOnly}";
        }
        JObject document = MakeDocument(MakeClip(stage));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                textEntryWithConfinedHostLora
                    ? fixture.Post(document, Customize)
                    : fixture.ImageToVideoPost(document, Customize));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        List<ComfyNode> loras = LoraLoaderNodesOf(bridge);
        AssertModelOnlyLora(loras, "UnitTest_Wan_Prompt.safetensors", 0.4);
        AssertModelOnlyLora(loras, "UnitTest_Wan_Persisted.safetensors", 0.6);
        Assert.Equal(2, loras.Count);

        LoraLoaderModelOnlyNode prompt = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>(),
            node => node.LoraName.LiteralAsString() == "UnitTest_Wan_Prompt.safetensors");
        LoraLoaderModelOnlyNode persisted = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>(),
            node => node.LoraName.LiteralAsString() == "UnitTest_Wan_Persisted.safetensors");
        Assert.Equal(
            Path.GetFileName(modelFixturePath),
            Assert.IsType<UNETLoaderNode>(prompt.Model.Connection?.Node)
                .UnetName.LiteralAsString());
        Assert.Same(prompt, persisted.Model.Connection?.Node);
        SwarmKSamplerNode stageSampler = StageSampler(bridge, 0);
        Assert.Same(persisted, stageSampler.Model.Connection?.Node);

        // The arms are different graph shapes, which the LoRA chain alone would never notice: 14B
        // conditions through WanImageToVideo, 5B through its own native latent.
        bool is5b = modelFixturePath == WanWorkflowFixture.Wan22Ti2v5bFixturePath;
        Assert.Equal(is5b ? 0 : 1, bridge.Graph.NodesOfType<WanImageToVideoNode>().Count);
        Assert.Equal(is5b ? 1 : 0, bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>().Count);

        // The request's own LoRA list is exactly what it was before the stages ran: the two the
        // prompt parser put there (a zero weight still counts) plus whatever the POST set. The
        // stage's LoRAs are borrowed and handed back, so they must not appear.
        Assert.Equal(
            textEntryWithConfinedHostLora
                ? ["UnitTest_Wan_Base_Confined", "UnitTest_Wan_Prompt", "UnitTest_Wan_PromptZero"]
                : new[] { "UnitTest_Wan_Prompt", "UnitTest_Wan_PromptZero" },
            generator.UserInput.Get(T2IParamTypes.Loras));
        // The borrowed host model-loader cache goes back too: StageModelLoadScope drops this key on
        // dispose whenever it applied a LoRA scope, so the loader it built under one cannot be
        // handed to a later consumer that is not under it.
        Assert.DoesNotContain(
            $"modelloader_{fixture.Model.Name}_image2video",
            generator.NodeHelpers.Keys);

        if (textEntryWithConfinedHostLora)
        {
            // Confined to the base pass, which a text-to-video request does not have — so it never
            // reaches the graph even though it is still on the request.
            Wan22ImageToVideoLatentNode latent = Assert.Single(
                bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
            Assert.False(latent.StartImage.HasValue);
            Assert.False(generator.IsImageToVideo);
            Assert.False(generator.IsImageToVideoSwap);
        }

        live.AssertAllLive(prompt, persisted, stageSampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Each stage's LoRAs are its own: two stages naming the same file at different weights get one
    /// loader each, and a third stage naming none samples straight off the shared UNET loader that
    /// both loaders also branch from — the loader is cached, the LoRA scope is not.
    /// </summary>
    [Fact]
    public async Task Stage_loras_do_not_leak_forward_and_share_one_cached_model_loader()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_Wan_Scoped_Lora.safetensors");
        JObject document = MakeDocument(MakeClip(
            WanWorkflowFixture.StageWithLoras(
                fixture.Stage(control: 1, steps: 10),
                WanWorkflowFixture.Lora("UnitTest_Wan_Scoped_Lora", 0.25)),
            WanWorkflowFixture.StageWithLoras(
                fixture.Stage("PreviousStage", control: 0.5, steps: 11),
                WanWorkflowFixture.Lora("UnitTest_Wan_Scoped_Lora", 0.75)),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        UNETLoaderNode loader = Assert.Single(bridge.Graph.NodesOfType<UNETLoaderNode>());
        LoraLoaderModelOnlyNode first = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>(),
            node => node.StrengthModel.LiteralAsDouble() == 0.25);
        LoraLoaderModelOnlyNode second = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>(),
            node => node.StrengthModel.LiteralAsDouble() == 0.75);
        Assert.Same(loader, first.Model.Connection?.Node);
        Assert.Same(loader, second.Model.Connection?.Node);

        Assert.Same(first, StageSampler(bridge, 0).Model.Connection?.Node);
        Assert.Same(second, StageSampler(bridge, 1).Model.Connection?.Node);
        Assert.Same(loader, StageSampler(bridge, 2).Model.Connection?.Node);
        Assert.Null(generator.UserInput.Get(T2IParamTypes.Loras));

        // The unscoped third stage leaves the loader cached, so a later consumer would reuse the
        // tuple as-is — every id in it must survive the cleanup the earlier stages triggered.
        AssertLoaderTupleIsLive(
            workflow,
            generator.NodeHelpers[$"modelloader_{fixture.Model.Name}_image2video"],
            loader);

        live.AssertAllLive(
            first, second, StageSampler(bridge, 0), StageSampler(bridge, 1),
            StageSampler(bridge, 2));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Prompt LoRA confinements pick out exactly the passes their selector names: a bare
    /// <c>&lt;videoclip&gt;</c> reaches every generating stage, <c>[0,1]</c> only that one stage,
    /// and <c>[1]</c> only the second clip. Each stage's branch is read by unwinding its model
    /// input, since every later stage also *reaches* its predecessor's LoRAs through the latent it
    /// refines.
    /// </summary>
    [Fact]
    public async Task Prompt_lora_confinements_select_exactly_the_passes_they_name()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        foreach (string name in new[] { "Bare", "Stage", "ClipWide" })
        {
            fixture.InstallModel("LoRA", $"UnitTest_Wan_{name}.safetensors");
        }
        JObject document = MakeDocument(
            MakeClip(
                fixture.Stage(steps: 10),
                fixture.Stage("PreviousStage", control: 0.5, steps: 11)),
            MakeClip(fixture.Stage(steps: 12)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            document,
            post => post["prompt"] =
                "global <videoclip><lora:UnitTest_Wan_Bare:0.2>"
                + " <videoclip[0,1]><lora:UnitTest_Wan_Stage:0.3>"
                + " <videoclip[1]><lora:UnitTest_Wan_ClipWide:0.4>");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        (UNETLoaderNode loader, string[] first) = ModelBranchOf(StageSampler(bridge, 0));
        (UNETLoaderNode secondLoader, string[] second) = ModelBranchOf(StageSampler(bridge, 1));
        (UNETLoaderNode thirdLoader, string[] third) = ModelBranchOf(StageSampler(bridge, 2));
        Assert.Same(loader, secondLoader);
        Assert.Same(loader, thirdLoader);
        Assert.Equal(["UnitTest_Wan_Bare.safetensors"], first);
        Assert.Equal(
            ["UnitTest_Wan_Bare.safetensors", "UnitTest_Wan_Stage.safetensors"],
            second);
        Assert.Equal(
            ["UnitTest_Wan_Bare.safetensors", "UnitTest_Wan_ClipWide.safetensors"],
            third);

        live.AssertAllLive(
            loader, StageSampler(bridge, 0), StageSampler(bridge, 1), StageSampler(bridge, 2));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A LoRA confined to a passthrough stage never loads — the stage has no pass to apply it to —
    /// and the unscoped stage after it samples straight off the shared checkpoint loader. The
    /// generating stage's own LoRA is the control that the confinement syntax works at all.
    /// </summary>
    [Fact]
    public async Task A_prompt_lora_confined_to_a_passthrough_stage_never_loads()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_Wan_Generating.safetensors");
        fixture.InstallModel("LoRA", "UnitTest_Wan_Passthrough.safetensors");
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(control: 1, steps: 10),
            fixture.Stage("PreviousStage", control: 0, steps: 11),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            document,
            post => post["prompt"] =
                "global <videoclip[0,0]><lora:UnitTest_Wan_Generating:0.3>"
                + " <videoclip[0,1]><lora:UnitTest_Wan_Passthrough:0.4>");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        (UNETLoaderNode loader, string[] first) = ModelBranchOf(StageSampler(bridge, 0));
        (UNETLoaderNode lastLoader, string[] last) = ModelBranchOf(StageSampler(bridge, 2));
        Assert.Equal(["UnitTest_Wan_Generating.safetensors"], first);
        Assert.Empty(last);
        Assert.Same(loader, lastLoader);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>(),
            lora => lora.LoraName.LiteralAsString() == "UnitTest_Wan_Passthrough.safetensors");

        live.AssertAllLive(loader, StageSampler(bridge, 0), StageSampler(bridge, 2));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A stage naming a LoRA that does not exist refuses the whole request, and everything the
    /// stage borrowed from the host is handed back on the way out: the request's LoRA lists, the
    /// stage's parameter section, and the cached model loader. The prompt-LoRA arm makes the
    /// rollback nested — one scope already applied when the next one throws.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_stage_lora_refuses_the_request_and_hands_back_host_state(
        bool withPromptLora)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_Wan_Present.safetensors");
        JObject stage = WanWorkflowFixture.StageWithLoras(
            fixture.Stage(steps: 10),
            WanWorkflowFixture.Lora("UnitTest_Wan_Missing", 0.45));
        WorkflowGenerator captured = null;
        List<string> lorasBeforeStages = null;

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(stage)),
                    post => post["prompt"] = withPromptLora
                        ? "global <videoclip[0,0]><lora:UnitTest_Wan_Present:0.4>"
                        : "global"),
                extraSteps:
                [
                    new(g =>
                    {
                        captured = g;
                        lorasBeforeStages = g.UserInput.Get(T2IParamTypes.Loras);
                    }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01),
                ]));

        Assert.Contains("UnitTest_Wan_Missing", error.Message);
        Assert.NotNull(captured);
        // Null-for-null, not []-for-null: the no-prompt-LoRA arm had no list at all, and the
        // stage's borrowed one must be removed rather than left behind as an empty list.
        Assert.Equal<IEnumerable<string>>(
            lorasBeforeStages,
            captured.UserInput.Get(T2IParamTypes.Loras));
        Assert.DoesNotContain(
            $"modelloader_{fixture.Model.Name}_image2video",
            captured.NodeHelpers.Keys);
        Assert.DoesNotContain(
            VideoStagesExtension.SectionIdForStage(0),
            captured.UserInput.SectionParamOverrides.Keys);
    }
}
