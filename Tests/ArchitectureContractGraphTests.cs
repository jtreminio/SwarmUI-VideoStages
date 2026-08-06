using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Contracts that only hold across a whole generated graph: two architectures each building an
/// independent subgraph behind one join, prompt-section LoRA confinement reaching the right stage,
/// the preflight gate, and the group toggle — all through the real Comfy API POST path.
/// <para>
/// A mixed-architecture timeline is the shape where a subgraph can be built correctly and then
/// silently orphaned, because nothing else in the request consumes it. That is why every join test
/// closes with <see cref="TypedWorkflowAssertions.AssertShippable"/> after asserting both stage
/// samplers live, rather than merely counting nodes.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public sealed class ArchitectureContractGraphTests
{
    /// <summary>At 24 fps a 0.6s clip is 14.4 frames, which LTX-2 and WAN both snap up to 17.</summary>
    private const int CrossFamilyFrames = 17;

    /// <summary>MiniMax H3's 17k+5 grid snaps the same 0.6s up to 22 instead.</summary>
    private const int MiniMaxCrossFamilyFrames = 22;

    /// <summary>A clip that generates nothing keeps the raw 0.6s at 24 fps, inclusive of both
    /// endpoints, with no frame grid to snap to.</summary>
    private const int SourceOnlyClipFrames = 16;

    private const double CrossFamilyClipDuration = 0.6;

    /// <summary>
    /// The family a stage sampler belongs to, read off the node feeding its latent — the one input
    /// every architecture builds for itself. Reachability would not do: a refine stage and a
    /// continue boundary both chain one sampler onto another's output.
    /// </summary>
    private static void AssertStageFamily(SwarmKSamplerNode sampler, string family)
    {
        ComfyNode latent = sampler.LatentImage.Connection?.Node;
        switch (family)
        {
            case "ltx2":
                Assert.IsType<LTXVConcatAVLatentNode>(latent);
                break;
            case "wan22":
                Assert.IsType<WanImageToVideoNode>(latent);
                break;
            case "minimax":
                // MiniMax keyframes ride the conditioning, so the joint latent reaches the sampler
                // unwrapped.
                Assert.IsType<EmptyMiniMaxH3LatentAVNode>(latent);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(family), family, null);
        }
    }

    /// <summary>The images a hard cut concatenates, in authored order.</summary>
    private static ComfyNode[] CutInputs(BatchImagesNodeNode cut) =>
        [.. Enumerable.Range(0, cut.Images.Count)
            .Select(index => cut.Images[index].Connection?.Node)];

    /// <summary>A hard cut is the neutral join: no crossfade composite, no ramp mask.</summary>
    private static BatchImagesNodeNode NeutralJoin(WorkflowBridge bridge)
    {
        Assert.Empty(bridge.Graph.NodesOfType<ImageCompositeMaskedNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        return Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
    }

    private static JObject FamilyClip(T2IModel model, string family, int steps) =>
        MakeClip(
            CrossFamilyClipDuration,
            MakeStage(
                model.Name,
                "Generated",
                steps: steps,
                // MiniMax H3 is guidance-distilled and is authored at cfg 1.
                cfgScale: family == "minimax" ? 1 : 4.5));

    /// <summary>
    /// Each clip's decoded output feeds exactly one side of the join, and neither side reaches the
    /// other clip's sampler. A subgraph that was built and then left unconsumed fails here.
    /// </summary>
    private static void AssertCutSeparatesTheStages(
        WorkflowBridge bridge,
        BatchImagesNodeNode cut,
        SwarmKSamplerNode firstStage,
        SwarmKSamplerNode secondStage)
    {
        ComfyNode[] cutInputs = CutInputs(cut);
        Assert.Equal(2, cutInputs.Length);
        Assert.True(ReachesUpstream(bridge, cutInputs[0], firstStage.Id));
        Assert.False(ReachesUpstream(bridge, cutInputs[0], secondStage.Id));
        Assert.True(ReachesUpstream(bridge, cutInputs[1], secondStage.Id));
        Assert.False(ReachesUpstream(bridge, cutInputs[1], firstStage.Id));
    }

    // ---- a sourced clip leading a generated one ------------------------------------------

    /// <summary>
    /// A clip that only carries uploaded footage has no architecture of its own, yet it still has to
    /// reach the join as one side of a cut — the other side being a real generated clip. The
    /// conformed source and the generated stage are separate subgraphs meeting only at the cut.
    /// </summary>
    [Theory]
    [InlineData("ltx2")]
    [InlineData("wan22")]
    public async Task Source_only_clip_can_lead_a_real_generated_clip_through_a_neutral_cut(
        string generatedFamily)
    {
        using VideoStagesWorkflowFixture fixture = generatedFamily == "ltx2"
            ? Ltx2WorkflowFixture.CreateWithBaseModel()
            : WanWorkflowFixture.CreateWithBaseModel();
        JObject sourced = MakeClip(CrossFamilyClipDuration);
        JObject source = SourceVideo();
        source["startSeconds"] = 1.0;
        sourced["initVideo"] = source;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(
                        sourced,
                        MakeClip(CrossFamilyClipDuration, fixture.Stage(steps: 9)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode sourceWindow = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        // 1.0s in, at 24 fps; the source clip is not snapped to a grid because it generates nothing.
        Assert.Equal(24, sourceWindow.StartFrame.LiteralAsInt());
        Assert.Equal(SourceOnlyClipFrames, sourceWindow.FrameCount.LiteralAsInt());

        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        AssertStageFamily(stage, generatedFamily);

        BatchImagesNodeNode cut = NeutralJoin(bridge);
        ComfyNode[] cutInputs = CutInputs(cut);
        Assert.Equal(2, cutInputs.Length);
        Assert.True(ReachesUpstream(bridge, cutInputs[0], sourceWindow.Id));
        Assert.False(ReachesUpstream(bridge, cutInputs[0], stage.Id));
        Assert.True(ReachesUpstream(bridge, cutInputs[1], stage.Id));
        Assert.False(ReachesUpstream(bridge, cutInputs[1], sourceWindow.Id));

        Assert.Equal(cut.Id, $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(
            SourceOnlyClipFrames + CrossFamilyFrames,
            generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio?.DataType);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path));

        live.AssertAllLive(sourceWindow, stage, cut);
        AssertShippable(bridge, workflow, live);
    }

    // ---- two architectures, one join ----------------------------------------------------

    /// <summary>
    /// An LTX clip and a WAN clip in one timeline build two independent subgraphs that meet only at
    /// the cut, in either authoring order.
    /// </summary>
    [Theory]
    [InlineData("ltx2", "wan22")]
    [InlineData("wan22", "ltx2")]
    public async Task Mixed_real_architectures_hard_cut_through_one_neutral_decoded_join(
        string firstFamily,
        string secondFamily)
    {
        using LtxAndWanFixture fixture = new();
        T2IModel first = firstFamily == "ltx2" ? fixture.Model : fixture.SecondModel;
        T2IModel second = firstFamily == "ltx2" ? fixture.SecondModel : fixture.Model;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(
                        FamilyClip(first, firstFamily, steps: 7),
                        FamilyClip(second, secondFamily, steps: 9)),
                    post => post["videomodel"] = first.Name));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstStage = StageSampler(bridge, 0);
        SwarmKSamplerNode secondStage = StageSampler(bridge, 1);
        Assert.Equal(7, firstStage.Steps.LiteralAsInt());
        Assert.Equal(9, secondStage.Steps.LiteralAsInt());
        AssertStageFamily(firstStage, firstFamily);
        AssertStageFamily(secondStage, secondFamily);

        BatchImagesNodeNode cut = NeutralJoin(bridge);
        AssertCutSeparatesTheStages(bridge, cut, firstStage, secondStage);

        Assert.Equal(cut.Id, $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(CrossFamilyFrames * 2, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio?.DataType);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path));

        live.AssertAllLive(firstStage, secondStage, cut);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The same claim across the two families that generate native audio, so the join reconciles two
    /// audio tracks as well as two video tracks. Their frame grids disagree (8k+1 against 17k+5), so
    /// the merged length is asymmetric.
    /// </summary>
    [Theory]
    [InlineData("ltx2", "minimax")]
    [InlineData("minimax", "ltx2")]
    public async Task Ltx_and_MiniMax_hard_cut_through_one_neutral_decoded_join(
        string firstFamily,
        string secondFamily)
    {
        using LtxAndMiniMaxFixture fixture = new();
        T2IModel first = firstFamily == "ltx2" ? fixture.Model : fixture.SecondModel;
        T2IModel second = firstFamily == "ltx2" ? fixture.SecondModel : fixture.Model;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(
                        FamilyClip(first, firstFamily, steps: 7),
                        FamilyClip(second, secondFamily, steps: 9)),
                    post => post["videomodel"] = first.Name));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstStage = StageSampler(bridge, 0);
        SwarmKSamplerNode secondStage = StageSampler(bridge, 1);
        AssertStageFamily(firstStage, firstFamily);
        AssertStageFamily(secondStage, secondFamily);

        BatchImagesNodeNode cut = NeutralJoin(bridge);
        AssertCutSeparatesTheStages(bridge, cut, firstStage, secondStage);

        Assert.Equal(cut.Id, $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(
            CrossFamilyFrames + MiniMaxCrossFamilyFrames,
            generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio?.DataType);

        live.AssertAllLive(firstStage, secondStage, cut);
        AssertShippable(bridge, workflow, live);
    }

    // ---- prompt-section LoRA confinement --------------------------------------------------

    private const string ClipLoraName = "UnitTest_VideoClipLora";

    private const string OrphanLoraName = "UnitTest_OrphanStageLora";

    private static Ltx2WorkflowFixture LoraFixture()
    {
        Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", $"{ClipLoraName}.safetensors");
        fixture.InstallModel("LoRA", $"{OrphanLoraName}.safetensors");
        return fixture;
    }

    /// <summary>The confinement section each parsed LoRA was tagged with, by LoRA name.</summary>
    private static string ConfinementOf(WorkflowGenerator generator, string loraName)
    {
        Assert.True(generator.UserInput.TryGet(T2IParamTypes.Loras, out List<string> loras));
        Assert.True(generator.UserInput.TryGet(
            T2IParamTypes.LoraSectionConfinement,
            out List<string> confinements));
        int index = loras.IndexOf(loraName);
        Assert.True(index >= 0, $"'{loraName}' was not parsed out of the prompt at all.");
        return confinements[index];
    }

    private static ComfyNode LoraLoaderFor(WorkflowBridge bridge, string loraName) =>
        Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node is LoraLoaderNode or LoraLoaderModelOnlyNode
                && node.FindInput("lora_name").LiteralAsString().Contains(loraName));

    /// <summary>
    /// A LoRA written into a <c>&lt;videoclip[1]&gt;</c> prompt section loads for that clip's stage
    /// and no other. Core's own confinement model-gen step does the work — the request never
    /// installs one of its own — so this is the real host behaviour, not a stand-in for it.
    /// </summary>
    [Fact]
    public async Task Videoclip_scoped_lora_applies_only_to_target_clip()
    {
        using Ltx2WorkflowFixture fixture = LoraFixture();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(
                        MakeClip(1.0, fixture.Stage(steps: 10)),
                        MakeClip(1.0, fixture.Stage(steps: 10))),
                    post => post["prompt"] =
                        $"global prompt <videoclip[1]><lora:{ClipLoraName}:0.5>"));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(
            $"{VideoStagesExtension.SectionIdForClip(1)}",
            ConfinementOf(generator, ClipLoraName));

        ComfyNode lora = LoraLoaderFor(bridge, ClipLoraName);
        // The only LoRA loader in the graph: one clip's confinement must not also build an inert
        // loader on the other clip's branch.
        Assert.Same(lora, Assert.Single(LoraLoaderNodesOf(bridge)));
        Assert.Equal(0.5, lora.FindInput("strength_model").LiteralAsDouble());
        Assert.False(ReachesUpstream(bridge, StageSampler(bridge, 0), lora.Id));
        Assert.True(ReachesUpstream(bridge, StageSampler(bridge, 1), lora.Id));

        live.AssertLive(lora);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The two-part <c>&lt;videoclip[clip,stage]&gt;</c> selector confines to the flat stage section
    /// instead of the clip section, and the stage it names still loads the LoRA.
    /// </summary>
    [Fact]
    public async Task Videoclip_bracket_clip_stage_prompt_lora_is_promoted_for_flat_stage_section()
    {
        using Ltx2WorkflowFixture fixture = LoraFixture();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(1.0, fixture.Stage(steps: 10))),
                    post => post["prompt"] =
                        $"global prompt <videoclip[0,0]><lora:{ClipLoraName}:0.5>"));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(
            $"{VideoStagesExtension.SectionIdForStage(0)}",
            ConfinementOf(generator, ClipLoraName));

        ComfyNode lora = LoraLoaderFor(bridge, ClipLoraName);
        Assert.True(ReachesUpstream(bridge, StageSampler(bridge, 0), lora.Id));

        live.AssertLive(lora);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A LoRA addressed to a stage the timeline does not have is confined to that stage's section
    /// and stays there: no loader is built for it, and the stage that does exist keeps only its own.
    /// </summary>
    [Fact]
    public async Task Videoclip_bracket_orphan_stage_lora_does_not_bubble_into_existing_sibling_stage()
    {
        using Ltx2WorkflowFixture fixture = LoraFixture();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(1.0, fixture.Stage(steps: 10))),
                    post => post["prompt"] = "global"
                        + $" <videoclip[0,0]><lora:{ClipLoraName}:1>"
                        + $" <videoclip[0,1]><lora:{OrphanLoraName}:1>"));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // A bracket selector naming a stage the timeline does not have resolves to the unmatched
        // section, which sits above the range any stage scope accepts.
        Assert.Equal(
            $"{Constants.SectionID_VideoClipUnmatched}",
            ConfinementOf(generator, OrphanLoraName));

        ComfyNode lora = LoraLoaderFor(bridge, ClipLoraName);
        Assert.True(ReachesUpstream(bridge, StageSampler(bridge, 0), lora.Id));
        Assert.DoesNotContain(
            bridge.Graph.Nodes.Values,
            node => node is LoraLoaderNode or LoraLoaderModelOnlyNode
                && node.FindInput("lora_name").LiteralAsString().Contains(OrphanLoraName));

        live.AssertLive(lora);
        AssertShippable(bridge, workflow, live);
    }

    // ---- the preflight gate and the group toggle -----------------------------------------

    /// <summary>An IC-LoRA clip driven by uploaded footage — the shape whose custom-node dependency
    /// preflight checks for.</summary>
    private static JObject IcLoraClip(Ltx2WorkflowFixture fixture)
    {
        JObject clip = MakeClip(1.0, fixture.Stage(steps: 10));
        clip["icLoras"] = new JArray(new JObject
        {
            ["lora"] = ClipLoraName,
            ["driveSource"] = MediaSource.Upload,
            ["driveData"] = "Visual",
            ["strength"] = 1.0,
            ["attentionStrength"] = 1.0,
            ["controlType"] = Constants.IcLoraControlNone,
            // ValidateParam rejects a media payload under 10 base64 characters.
            ["driveMedia"] = new JObject
            {
                ["data"] = "data:video/mp4;base64,AAAAAAAAAAAAAAAAAAAAAA==",
                ["fileName"] = "drive.mp4",
            },
        });
        return clip;
    }

    /// <summary>
    /// Preflight is not a blanket rejection of IC-LoRA timelines: with the custom nodes present the
    /// same request generates normally, and with the group toggled off it is inert — the config JSON
    /// still rides the hidden Data param, only the Enabled flag is absent.
    /// <para>
    /// The rejection half stays in <see cref="RequestPreflightPhaseTests"/>: the API harness always
    /// reports the LTX-2 node feature, so the missing-node case is unreachable from a POST.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ic_lora_stage_builds_its_loader_chain_only_while_the_group_is_enabled(
        bool enabled)
    {
        using Ltx2WorkflowFixture fixture = LoraFixture();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(IcLoraClip(fixture)),
                    post =>
                    {
                        if (!enabled)
                        {
                            post.Remove("videostagesenabled");
                        }
                    }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        if (enabled)
        {
            LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
                bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
            LTXAddVideoICLoRAGuideNode guide = Assert.Single(
                bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
            Assert.True(ReachesUpstream(bridge, StageSampler(bridge, 0), loader.Id));
            live.AssertAllLive(loader, guide);
        }
        else
        {
            Assert.Empty(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
            Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
            Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        }
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The config JSON is always sent; only the group's Enabled flag disappears when the user toggles
    /// the group off. A two-stage clip is the discriminator: enabled it contributes two samplers at
    /// the timeline's own seeds, disabled the host generates alone.
    /// <para>
    /// Both request shapes are asserted. Text-to-video pins that core's root is swept when the
    /// timeline takes over; image-to-video pins that a toggled-off group leaves core's own base pass
    /// <em>and</em> its video root standing, which the text shape cannot show because it has no base
    /// pass to confuse a sampler count with.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Section_present_but_group_toggle_off_does_not_activate_video_stages(
        bool imageToVideo)
    {
        using Ltx2WorkflowFixture fixture = imageToVideo
            ? Ltx2WorkflowFixture.CreateWithBaseModel()
            : Ltx2WorkflowFixture.Create();
        JObject document = MakeDocument(
            MakeClip(1.0, fixture.Stage(steps: 10), fixture.Stage(control: 0.5, steps: 10)));
        JObject Post(Action<JObject> customize) => imageToVideo
            ? fixture.ImageToVideoPost(document, customize)
            : fixture.Post(document, customize);

        JObject enabledWorkflow = await ComfyWorkflowApiTestHarness.GenerateAsync(Post(null));
        using (WorkflowBridge enabledBridge = WorkflowBridge.Create(enabledWorkflow))
        {
            // Image-to-video keeps core's base pass alongside the two stages.
            Assert.Equal(
                imageToVideo ? 3 : 2,
                enabledBridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
            StageSampler(enabledBridge, 0);
            StageSampler(enabledBridge, 1);
        }

        JObject disabledWorkflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            Post(post => post.Remove("videostagesenabled")));
        using WorkflowBridge disabledBridge = WorkflowBridge.Create(disabledWorkflow);
        WorkflowLivePath live = WorkflowLivePath.For(disabledBridge);

        List<SwarmKSamplerNode> hosts = [.. disabledBridge.Graph.NodesOfType<SwarmKSamplerNode>()];
        SwarmKSamplerNode host = Assert.Single(
            hosts,
            sampler => sampler.NoiseSeed.LiteralAsLong() == VideoStagesWorkflowFixture.Seed);
        // Core's base pass plus core's own single video root, both untouched. Seeds cannot
        // discriminate here — core's image-to-video sampler seeds Seed + 42, which is also stage
        // 0's seed — so the count is the claim: the second authored stage produced nothing.
        Assert.Equal(imageToVideo ? 2 : 1, hosts.Count);
        live.AssertLive(host);
        AssertShippable(disabledBridge, disabledWorkflow, live);
    }

    /// <summary>
    /// Prompt-tag dimension overrides ride ExtraMeta, which the global prompt processor populates
    /// even for a toggled-off group. Enabled they beat both the document's own resolution and the
    /// request's; disabled the request's resolution stands and no stage runs at all.
    /// </summary>
    [Fact]
    public async Task Dimension_override_tags_do_not_resolve_root_size_when_group_toggle_off()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject document = MakeRootConfig(800, 600, MakeClip(1.0, fixture.Stage(steps: 10)));
        const string Prompt = "cat <videostages[width]:1024> <videostages[height]:1024>";

        JObject enabledWorkflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(document, post => post["prompt"] = Prompt));
        using (WorkflowBridge enabledBridge = WorkflowBridge.Create(enabledWorkflow))
        {
            EmptyLTXVLatentVideoNode latent = Assert.Single(
                enabledBridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
            Assert.Equal(1024, latent.Width.LiteralAsInt());
            Assert.Equal(1024, latent.Height.LiteralAsInt());
            StageSampler(enabledBridge, 0);
        }

        JObject disabledWorkflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(document, post =>
            {
                post["prompt"] = Prompt;
                post.Remove("videostagesenabled");
            }));
        using WorkflowBridge disabledBridge = WorkflowBridge.Create(disabledWorkflow);
        WorkflowLivePath live = WorkflowLivePath.For(disabledBridge);

        EmptyLTXVLatentVideoNode hostLatent = Assert.Single(
            disabledBridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(VideoStagesWorkflowFixture.Width, hostLatent.Width.LiteralAsInt());
        Assert.Equal(VideoStagesWorkflowFixture.Height, hostLatent.Height.LiteralAsInt());
        SwarmKSamplerNode host = Assert.Single(
            disabledBridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(VideoStagesWorkflowFixture.Seed, host.NoiseSeed.LiteralAsLong());

        live.AssertLive(hostLatent);
        AssertShippable(disabledBridge, disabledWorkflow, live);
    }

    // ---- the shared root VAE loader ------------------------------------------------------

    /// <summary>
    /// Regression for "Node 101 not found": the displaced-root sweep runs while both clips' decodes
    /// still read the root VAE loader, and a sweep that walked only node inputs would prune it out
    /// from under them. Asserted structurally — the loader is whichever node both decodes share.
    /// </summary>
    [Theory]
    [InlineData("cut")]
    [InlineData("crossfade")]
    public async Task Multi_clip_merge_keeps_the_shared_root_vae_loader_the_clip_decodes_read(
        string boundaryOut)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = MakeClip(CrossFamilyClipDuration, fixture.Stage(steps: 10));
        first["boundaryOut"] = boundaryOut;
        first["boundaryOutOverlap"] = 8;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(
                    MakeDocument(
                        first,
                        MakeClip(CrossFamilyClipDuration, fixture.Stage(steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        VAEDecodeTiledNode[] decodes = [.. bridge.Graph.NodesOfType<VAEDecodeTiledNode>()];
        Assert.Equal(2, decodes.Length);
        Assert.True(ReachesUpstream(bridge, decodes[0], StageSampler(bridge, 0).Id));
        Assert.True(ReachesUpstream(bridge, decodes[1], StageSampler(bridge, 1).Id));

        VAELoaderNode rootVae = Assert.IsType<VAELoaderNode>(decodes[0].Vae.Connection?.Node);
        Assert.Same(rootVae, decodes[1].Vae.Connection?.Node);

        live.AssertAllLive(rootVae, decodes[0], decodes[1]);
        AssertShippable(bridge, workflow, live);
    }
}
