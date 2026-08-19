using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Authoring;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class Ltx2GeneratedWorkflowContractTests
{
    /// <summary>
    /// The smoke test the LTX-2 conversion chunks rest on: an LTX-2.3 request survives the whole
    /// production step list, resolves all four support-model stubs without reaching for a
    /// download, and hands the timeline's sampler a joint audio/video latent.
    /// </summary>
    [Fact]
    public async Task Basic_text_to_video_can_be_generated_from_the_Comfy_API_POST_body()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = MakeClip(1.0, fixture.Stage());

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // The timeline hands the request back decoded video, not the latent it sampled.
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);

        EmptyLTXVLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(fixture.ExpectedGeneratedFrames, latent.Length.LiteralAsInt());

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(Ltx2WorkflowFixture.Steps, sampler.Steps.LiteralAsInt());
        LTXVConcatAVLatentNode joint = JointLatentOf(sampler);
        Assert.Same(latent, joint.VideoLatent.Connection?.Node);

        // The 2.3 branch: a dual CLIP loader (Gemma + text projection) and a separate audio VAE.
        Assert.Single(bridge.Graph.NodesOfType<DualCLIPLoaderNode>());
        Assert.Single(bridge.Graph.NodesOfType<SwarmLTXVAudioVAELoaderNode>());
        LTXVAudioVAEDecodeNode audioDecode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>());

        live.AssertAllLive(latent, joint, sampler, audioDecode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The text-to-video shape is what makes this observable: core's own encoders are in
    /// <c>AutoCleanupNodeTypes</c> and get pruned, so the encoder feeding the stage sampler is the
    /// only one carrying a prompt. Under image-to-video the base model would encode the global
    /// prompt into an encoder of its own and "the global text is absent" could not be asserted.
    /// </summary>
    [Theory]
    // No section matches the stage, so the un-sectioned global text carries it.
    [InlineData("global-only words", "global-only words")]
    // A clip section displaces the global text rather than adding to it.
    [InlineData("global-only words <videoclip[0]>clip-zero words", "clip-zero words")]
    // The request-wide video section displaces it the same way.
    [InlineData("global-only words <video>video-only words", "video-only words")]
    // Every section matching the stage concatenates: the clip's and the stage's, still not the global.
    [InlineData(
        "global-only words <videoclip[0]>clip-zero words <videoclip[0,0]>stage-zero words",
        "clip-zero words stage-zero words")]
    public async Task Stage_conditioning_carries_only_the_sections_that_match_it(
        string prompt,
        string expected)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = MakeClip(1.0, fixture.Stage());

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["prompt"] = prompt);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        LTXVConditioningNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConditioningNode>());
        Assert.Same(conditioning, sampler.Positive.Connection?.Node);

        SwarmClipTextEncodeAdvancedNode positive = Assert.IsType<SwarmClipTextEncodeAdvancedNode>(
            conditioning.PositiveInput.Connection?.Node);
        Assert.Equal(expected, positive.Prompt.LiteralAsString());
        // Exact equality only pins what this one encoder carries; the global text must also be
        // absent from every other encoder, including the negative one.
        Assert.Single(
            bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>(),
            encoder => !string.IsNullOrWhiteSpace(encoder.Prompt.LiteralAsString()));

        live.AssertAllLive(positive, conditioning, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Confinement is core's own model-gen step, so only the production step list scopes a LoRA to
    /// the clip that authored it. LTX-2 leaves <c>LorasTargetTextEnc</c> at its <c>true</c> default
    /// — unlike MiniMax, which opts out — which is why this is a <c>LoraLoader</c> carrying a
    /// strength_clip rather than a <c>LoraLoaderModelOnly</c>.
    /// </summary>
    [Fact]
    public async Task A_clip_scoped_lora_patches_only_its_own_clip()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_VideoClipLora.safetensors");

        JObject plain = MakeClip(1.0, fixture.Stage());
        JObject loraClip = MakeClip(1.0, fixture.Stage());
        loraClip["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_VideoClipLora",
            ["weight"] = 0.5,
        });

        JObject workflow = await fixture.GenerateAsync(MakeDocument(plain, loraClip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LoraLoaderNode lora = Assert.Single(bridge.Graph.NodesOfType<LoraLoaderNode>());
        Assert.Equal("UnitTest_VideoClipLora.safetensors", lora.LoraName.LiteralAsString());
        Assert.Equal(0.5, lora.StrengthModel.LiteralAsDouble());
        Assert.Equal(0.5, lora.StrengthClip.LiteralAsDouble());
        Assert.Empty(bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>());

        SwarmKSamplerNode plainSampler = StageSampler(bridge, 0);
        SwarmKSamplerNode loraSampler = StageSampler(bridge, 1);
        Assert.False(ReachesUpstream(bridge, plainSampler, lora.Id));
        Assert.True(ReachesUpstream(bridge, loraSampler, lora.Id));

        live.AssertAllLive(lora, plainSampler, loraSampler);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_clip_lora_patches_both_halves_of_its_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_VideoClipStageLora.safetensors");

        JObject stage = fixture.Stage();
        JObject clip = MakeClip(1.0, stage);
        clip["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_VideoClipStageLora",
            ["weight"] = 0.5,
        });
        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LoraLoaderNode lora = Assert.Single(bridge.Graph.NodesOfType<LoraLoaderNode>());
        Assert.Equal("UnitTest_VideoClipStageLora.safetensors", lora.LoraName.LiteralAsString());

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.True(ReachesUpstream(bridge, sampler.Model.Connection?.Node, lora.Id));
        // LorasTargetTextEnc: the patched CLIP has to reach the encoders too, not just the model.
        // The count guard keeps this from passing on an empty conditioning chain.
        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders =
            [.. bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>()];
        Assert.Equal(2, encoders.Count);
        Assert.All(encoders, encoder => Assert.Same(lora, encoder.Clip.Connection?.Node));

        live.AssertAllLive(lora, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A drive source with no ControlNet configured is not a refusal: the resolver warns and applies
    /// the model patch without a guide.
    /// </summary>
    [Fact]
    public async Task An_ic_lora_patches_the_stage_model_through_the_ltx_ic_loader()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_ControlNetLora.safetensors");

        JObject clip = MakeClip(1.0, fixture.Stage());
        clip["icLoras"] = new JArray(new JObject
        {
            ["lora"] = "UnitTest_ControlNetLora",
            ["driveSource"] = MediaSource.ControlNetOne,
            ["driveData"] = $"{IcLoraDriveData.Visual}",
        });

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode icLora = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Equal("UnitTest_ControlNetLora.safetensors", icLora.LoraName.LiteralAsString());
        // An IC-LoRA is never a plain LoRA: it patches through its own loader or not at all.
        Assert.Empty(LoraLoaderNodesOf(bridge));

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(icLora, sampler.Model.Connection?.Node);

        live.AssertAllLive(icLora, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// One short MINOR window over the opening of the clip; the remainder is a gap the node fills
    /// with the global (MAJOR) prompt — two tiled windows, so the relay activates.
    /// </summary>
    [Fact]
    public async Task A_minor_prompt_window_and_its_gap_emit_a_wired_prompt_relay()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = MakeClip(4.0, fixture.Stage());

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["prompt"] = "global words <videoclip[0]:0-0.25>a red car");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmPromptRelayEncodeNode relay = Assert.Single(
            bridge.Graph.NodesOfType<SwarmPromptRelayEncodeNode>());
        Assert.Equal("global words", relay.GlobalPrompt.LiteralAsString());
        // Also the node's codegen default, so on an extension-built node this catches the
        // extension's constant drifting away from it, not that production wrote it.
        Assert.Equal(0.001, relay.Epsilon.LiteralAsDouble());
        Assert.NotNull(relay.ModelInput.Connection);
        Assert.NotNull(relay.Clip.Connection);

        // The node is told the sampler's real latent geometry rather than deriving it from the
        // window seconds: 4s at 24 fps is 97 frames, which is (97-1)/8+1 = 13 latent frames.
        EmptyLTXVLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(97, latent.Length.LiteralAsInt());
        JObject payload = JObject.Parse(relay.Windows.LiteralAsString());
        Assert.Equal(13, payload.Value<int>("latentFrames"));

        // The tiled window list leads with the MINOR prompt, then a blank gap window.
        JArray windows = (JArray)payload["windows"];
        Assert.Equal(2, windows.Count);
        Assert.Equal("a red car", (string)windows[0]["prompt"]);
        // Seconds are emitted through Math.Round(_, 1), so the authored 0.25 lands on 0.2 —
        // banker's rounding, not truncation. The frame-exact value rides in latentFrames instead.
        Assert.Equal(0.2, (double)windows[0]["seconds"], precision: 3);
        Assert.Equal("", (string)windows[1]["prompt"]);

        // Positive conditioning comes off the relay's own output (slot 1); negative still comes
        // from a plain advanced encoder.
        LTXVConditioningNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConditioningNode>());
        Assert.Same(relay, conditioning.PositiveInput.Connection?.Node);
        Assert.Equal(1, conditioning.PositiveInput.Connection.SlotIndex);
        Assert.IsType<SwarmClipTextEncodeAdvancedNode>(conditioning.NegativeInput.Connection?.Node);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(conditioning, sampler.Positive.Connection?.Node);

        live.AssertAllLive(relay, latent, conditioning, sampler);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_clip_with_no_prompt_windows_emits_no_relay()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = MakeClip(4.0, fixture.Stage());

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["prompt"] = "global words");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmPromptRelayEncodeNode>());

        // Without this the test stays green if the whole conditioning chain disappears.
        LTXVConditioningNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConditioningNode>());
        SwarmClipTextEncodeAdvancedNode positive = Assert.IsType<SwarmClipTextEncodeAdvancedNode>(
            conditioning.PositiveInput.Connection?.Node);
        Assert.Equal("global words", positive.Prompt.LiteralAsString());

        live.AssertAllLive(positive, conditioning);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A continue target opens with a hidden handle it does not publish, so its authored windows
    /// must be pushed behind a blank one of the handle's length.
    /// </summary>
    [Fact]
    public async Task A_continue_target_delays_its_prompt_windows_behind_the_hidden_handle()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject lead = MakeClip(0.6, fixture.Stage());
        lead["boundaryOut"] = Constants.BoundaryOutContinue;
        JObject target = MakeClip(0.6, fixture.Stage());

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(lead, target),
            post => post["prompt"] = "global words <videoclip[1]:0-0.25>target opening");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // 0.6s at 24 fps aligns to 17 authored frames; the target adds the 8-frame handle.
        EmptyLTXVLatentVideoNode targetLatent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>(),
            node => node.Length.LiteralAsInt() == 17 + 8);

        SwarmPromptRelayEncodeNode relay = Assert.Single(
            bridge.Graph.NodesOfType<SwarmPromptRelayEncodeNode>());
        JObject payload = JObject.Parse(relay.Windows.LiteralAsString());
        Assert.Equal(4, payload.Value<int>("latentFrames"));

        JArray windows = (JArray)payload["windows"];
        Assert.Equal(3, windows.Count);
        Assert.Equal("", windows[0].Value<string>("prompt"));
        // Math.Round(8/24, 1) — the handle's frame-exact length is pinned by latentFrames above.
        Assert.Equal(0.3, windows[0].Value<double>("seconds"), precision: 3);
        Assert.Equal("target opening", windows[1].Value<string>("prompt"));

        SwarmKSamplerNode targetSampler = StageSampler(bridge, 1);
        // Reachability from the sampler itself would also hold for a relay wired onto clip 0 — the
        // continue boundary puts all of clip 0 upstream. Only its conditioning arm is the claim.
        Assert.True(ReachesUpstream(bridge, targetSampler.Positive.Connection?.Node, relay.Id));

        live.AssertAllLive(relay, targetLatent, targetSampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A window covering the whole clip tiles to a single segment — below the two-window gate — so
    /// no relay is built, but its prompt still replaces the MAJOR prompt on the plain encode.
    /// </summary>
    [Fact]
    public async Task A_single_full_span_window_replaces_the_prompt_without_a_relay()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = MakeClip(4.0, fixture.Stage());

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["prompt"] = "global words <videoclip[0]:0-5>whole clip");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmPromptRelayEncodeNode>());

        LTXVConditioningNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConditioningNode>());
        SwarmClipTextEncodeAdvancedNode positive = Assert.IsType<SwarmClipTextEncodeAdvancedNode>(
            conditioning.PositiveInput.Connection?.Node);
        Assert.Equal("whole clip", positive.Prompt.LiteralAsString());

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(conditioning, sampler.Positive.Connection?.Node);

        live.AssertAllLive(positive, conditioning, sampler);
        AssertShippable(bridge, workflow, live);
    }
}
