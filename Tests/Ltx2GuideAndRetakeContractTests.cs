using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Guide-reference selection and retake masking, generated through the real Comfy API POST path.
/// </summary>
[Collection("VideoStagesTests")]
public class Ltx2GuideAndRetakeContractTests
{
    /// <summary>
    /// The image feeding the in-place guide a stage sampler consumes. Node ids are allocation
    /// order, not stage order, so the guide has to be reached through the sampler that uses it.
    /// </summary>
    private static ComfyNode GuideImageOf(SwarmKSamplerNode sampler)
    {
        LTXVConcatAVLatentNode joint = Assert.IsType<LTXVConcatAVLatentNode>(
            sampler.LatentImage.Connection?.Node);
        LTXVImgToVideoInplaceNode guide = Assert.IsType<LTXVImgToVideoInplaceNode>(
            joint.VideoLatent.Connection?.Node);
        ComfyNode guideImage = guide.Image.Connection?.Node;
        // Without this the reachability assertions pass vacuously on a dropped reference.
        Assert.NotNull(guideImage);
        return guideImage;
    }

    /// <summary>
    /// The mask block lengths in the order <c>LtxVideoRetakeMasker</c> appends them — prefix,
    /// window, suffix. Only the batch node's own input list carries that order; node ids are
    /// allocation order.
    /// </summary>
    private static int[] RetakeMaskBlocks(LTXVSetVideoLatentNoiseMasksNode maskNode)
    {
        ImageToMaskNode toMask = Assert.IsType<ImageToMaskNode>(maskNode.Masks.Connection?.Node);
        ComfyNode masked = toMask.Image.Connection?.Node;
        IEnumerable<ComfyNode> blocks = masked is BatchImagesNodeNode batch
            ? batch.Images.Items.Select(image => image.Connection?.Node)
            : [masked];
        return [.. blocks.Select(block =>
            Assert.IsType<RepeatImageBatchNode>(block).Amount.LiteralAsInt() ?? 0)];
    }

    /// <summary>
    /// The clip's framing mode replaces the plain <c>ImageScale</c> that fits a root reference to
    /// the clip resolution.
    /// </summary>
    [Fact]
    public async Task Authored_guide_uses_the_clips_green_fit_framing()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, fixture.Stage("Base"));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

        // Non-square on purpose: SwarmFrameImage defaults to 512x512, which is the fixture's own
        // resolution, so at that size the geometry below would hold even unwritten.
        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip), post =>
        {
            post["width"] = 768;
            post["height"] = 448;
        });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameImageNode frame = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameImageNode>());
        Assert.Equal(Constants.ReferenceFramingFitGreen, frame.Method.LiteralAsString());
        Assert.Equal(768, frame.Width.LiteralAsInt());
        Assert.Equal(448, frame.Height.LiteralAsInt());
        ComfyNode framed = frame.ImagesInput.Connection?.Node;
        Assert.NotNull(framed);
        AssertImageSource(framed, "images");

        // The captured Base reference is a latent, so a decode sits between it and the framing node.
        // Only the endpoints are contractual; the length of the chain is not.
        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.True(
            ReachesUpstream(bridge, GuideImageOf(sampler), frame.Id),
            "The stage guide does not trace back through the clip's framing node.");

        live.AssertAllLive(frame, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Base is captured at priority -4.2 — the base sampler's <em>latent</em>, before the refiner —
    /// and Refiner at 5.89, the decoded image after it. <c>LtxStageGuideMediaResolver</c> turns the
    /// latent into an image by walking downstream to the nearest VAEDecode of that same latent.
    /// <para>
    /// The refiner here is the default kind — <c>PostApply</c>, no upscale — so core materialises no
    /// pre-refiner decode at all and the only decode in the graph is the refiner's. A walk that
    /// crossed the refiner sampler would land on it and make the two references indistinguishable.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public async Task Root_reference_separates_base_from_refiner_when_core_decodes_the_base_pass(
        string source)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, fixture.Stage(), fixture.Stage(source));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(clip),
            post =>
            {
                // Core's refiner step is gated on BOTH of these being present.
                post["refinermethod"] = "PostApply";
                post["refinercontrolpercentage"] = 0.2;
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode baseSampler = fixture.BaseSampler(bridge);
        SwarmKSamplerNode refinerSampler = fixture.RefinerSampler(bridge);
        SwarmKSamplerNode stageSampler = StageSampler(bridge, 1);

        ComfyNode guideImage = GuideImageOf(stageSampler);
        AssertImageSource(guideImage, "image");
        Assert.Equal(
            source == "Refiner",
            ReachesUpstream(bridge, guideImage, refinerSampler.Id));
        // The refiner refines the base pass, so both references trace back to the base sampler.
        Assert.True(ReachesUpstream(bridge, guideImage, baseSampler.Id));

        live.AssertAllLive(baseSampler, refinerSampler, stageSampler);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Authored_stage_reference_drives_the_selected_stage_guide()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, fixture.Stage("Base"), fixture.Stage("Stage0"));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode stage0 = StageSampler(bridge, 0);
        SwarmKSamplerNode stage1 = StageSampler(bridge, 1);

        Assert.True(
            ReachesUpstream(bridge, GuideImageOf(stage1), stage0.Id),
            "Stage0's selected guide does not trace to this clip's first stage.");

        live.AssertAllLive(stage0, stage1);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// StageIds are flat across clips (0/1 then 2/3), but a <c>Stage0</c> reference is clip-local.
    /// </summary>
    [Fact]
    public async Task Stage_reference_numbers_are_local_to_each_clip()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = MakeClip(1.0, fixture.Stage("Base"), fixture.Stage("Stage0"));
        JObject second = MakeClip(1.0, fixture.Stage("Base"), fixture.Stage("Stage0"));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode clip0Stage0 = StageSampler(bridge, 0);
        SwarmKSamplerNode clip1Stage0 = StageSampler(bridge, 2);
        SwarmKSamplerNode clip1Stage1 = StageSampler(bridge, 3);

        ComfyNode guideImage = GuideImageOf(clip1Stage1);
        Assert.True(
            ReachesUpstream(bridge, guideImage, clip1Stage0.Id),
            "Clip 1 Stage0 did not resolve to clip 1's first stage.");
        Assert.False(
            ReachesUpstream(bridge, guideImage, clip0Stage0.Id),
            "Clip 1 Stage0 incorrectly resolved to clip 0's first stage.");

        live.AssertAllLive(clip0Stage0, clip1Stage0, clip1Stage1);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A source-video clip whose stage 0 explicitly asks for <c>Generated</c> keeps core's root
    /// video as its guide; the footage stays the latent it refines, not the image it is guided by.
    /// </summary>
    [Fact]
    public async Task InitVideo_stage_zero_explicit_generated_reference_uses_the_captured_root()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(0.6, fixture.Stage("Generated", control: 0.5));
        clip["initVideo"] = SourceVideo();

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode initVideoWindow = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        // A Generated reference is what keeps core's root video alive, so its empty latent is a
        // stable handle on the retained host root.
        EmptyLTXVLatentVideoNode hostRootLatent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());

        // Not StageSampler: core's retained image-to-video root sampler also seeds Seed + 42, so
        // stage 0's seed is ambiguous here. Reachability to the source footage is not.
        SwarmKSamplerNode stage = Assert.Single(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            sampler => ReachesUpstream(bridge, sampler, initVideoWindow.Id));
        ComfyNode guideImage = GuideImageOf(stage);
        Assert.True(
            ReachesUpstream(bridge, guideImage, hostRootLatent.Id),
            "The explicit Generated guide does not trace to the retained host root video.");
        Assert.False(
            ReachesUpstream(bridge, guideImage, initVideoWindow.Id),
            "The explicit Generated guide was replaced by the init-video footage.");

        live.AssertAllLive(initVideoWindow, hostRootLatent, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A retake stage samples from step 0 whatever its authored control says: the per-frame noise
    /// mask, not StartStep, decides what regenerates.
    /// </summary>
    [Fact]
    public async Task Retake_window_attaches_noise_mask_and_forces_full_start_step()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = RetakeClip(
            new JObject
            {
                ["startSeconds"] = 1.0,
                ["lengthSeconds"] = 1.0,
                ["strength"] = 0.8,
            },
            fixture.Stage(control: 0.5, steps: 10));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["text2videoframes"] = Ltx2WorkflowFixture.RetakeClipFrames);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVSetVideoLatentNoiseMasksNode maskNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        SwarmLoadVideoB64Node loadVideo = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        // Control 0.5 over 10 steps would start at 5; the retake overrides that to 0.
        Assert.Equal(10, sampler.Steps.LiteralAsInt());
        Assert.Equal(0, sampler.StartAtStep.LiteralAsInt());
        // Stage 0 here carries no frame reference, so nothing builds an in-place guide; this pins
        // that the retake path adds none of its own. Retake does NOT suppress one — keep the
        // reference and production emits an LTXVImgToVideoInplace alongside the mask.
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.True(
            ReachesUpstream(bridge, sampler.LatentImage.Connection?.Node, maskNode.Id),
            "Sampler latent input does not trace upstream to the retake noise-mask node.");
        Assert.True(
            ReachesUpstream(bridge, maskNode.Samples.Connection?.Node, loadVideo.Id),
            "Retake noise-mask samples input does not trace upstream to the loaded source video.");

        live.AssertAllLive(maskNode, loadVideo, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Frames [24, 48) of 97 map to latent frames [3, 6) of 13. The mask ladder is built from stock
    /// primitives that are not in core's <c>AutoCleanupNodeTypes</c>, so a misbuilt one survives to
    /// Comfy — <c>AssertNoOrphanNodes</c> is the guard against that.
    /// </summary>
    [Fact]
    public async Task Retake_mask_block_lengths_match_window()
    {
        const double strength = 0.75;
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = RetakeClip(
            new JObject
            {
                ["startSeconds"] = 1.0,
                ["lengthSeconds"] = 1.0,
                ["strength"] = strength,
            },
            fixture.Stage(steps: 10));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["text2videoframes"] = Ltx2WorkflowFixture.RetakeClipFrames);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVSetVideoLatentNoiseMasksNode maskNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());

        Assert.Equal([3, 3, 7], RetakeMaskBlocks(maskNode));

        // Exactly the window block carries the retake strength; the frozen blocks are value 0, and
        // one zero mask serves both of them because a mask of the same size and value is the same
        // mask. The upstream filter matters: the audio latent carries a full-frame SolidMask of
        // its own.
        List<SolidMaskNode> solids = [.. bridge.Graph.NodesOfType<SolidMaskNode>()
            .Where(solid => ReachesUpstream(bridge, maskNode, solid.Id))];
        Assert.Single(solids, solid => solid.Value.LiteralAsDouble() == strength);
        Assert.Single(solids, solid => solid.Value.LiteralAsDouble() == 0.0);

        // The joint audio/video window spans the same latent block, whose boundary pixels are 17/41.
        LTXVSetAudioVideoMaskByTimeNode maskByTime = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioVideoMaskByTimeNode>());
        Assert.Equal(17.0 / 24, maskByTime.StartTime.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(41.0 / 24, maskByTime.EndTime.LiteralAsDouble()!.Value, precision: 6);

        live.AssertAllLive(maskNode, maskByTime);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A retake on a later stage masks the previous stage's latent directly. Core's post-cleanup
    /// collapses same-VAE decode/encode pairs, so this asserts the combined result: no round trip
    /// survives between the two samplers, however it got removed.
    /// </summary>
    [Fact]
    public async Task Final_stage_retake_masks_the_previous_stage_latent_without_a_vae_round_trip()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = RetakeClip(
            new JObject
            {
                ["startSeconds"] = 1.0,
                ["lengthSeconds"] = 1.0,
                ["strength"] = 0.8,
            },
            fixture.Stage(control: 0.5, steps: 8),
            fixture.Stage(control: 0.5, steps: 10));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["text2videoframes"] = Ltx2WorkflowFixture.RetakeClipFrames);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode last = StageSampler(bridge, 1);
        LTXVSetVideoLatentNoiseMasksNode mask = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());

        Assert.True(ReachesUpstream(bridge, mask.Samples.Connection?.Node, first.Id));
        Assert.True(ReachesUpstream(bridge, last.LatentImage.Connection?.Node, mask.Id));
        Assert.DoesNotContain(
            bridge.Graph.Nodes.Values,
            node => (node is VAEDecodeNode or VAEDecodeTiledNode or VAEEncodeNode)
                && ReachesUpstream(bridge, node, first.Id)
                && ReachesUpstream(bridge, last.LatentImage.Connection?.Node, node.Id));

        live.AssertAllLive(first, last, mask);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Duration 4.0s at 24 fps aligns UP to 97 frames (4.042s). A retake ending exactly at the
    /// authored 4.0s must still regenerate through the aligned tail — no frozen suffix block.
    /// </summary>
    [Fact]
    public async Task Retake_reaching_clip_end_covers_the_aligned_tail()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = RetakeClip(
            new JObject
            {
                ["startSeconds"] = 2.0,
                ["lengthSeconds"] = 2.0,
                ["strength"] = 0.9,
            },
            fixture.Stage(control: 0.5, steps: 10));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["text2videoframes"] = Ltx2WorkflowFixture.RetakeClipFrames);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVSetVideoLatentNoiseMasksNode maskNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        // Latents [6, 13) of 13 regenerate: prefix 6, window 7, and NO frozen suffix block.
        Assert.Equal([6, 7], RetakeMaskBlocks(maskNode));

        // The joint audio/video window runs to the aligned video end, past the authored 4.0s.
        LTXVSetAudioVideoMaskByTimeNode maskByTime = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioVideoMaskByTimeNode>());
        Assert.Equal(41.0 / 24, maskByTime.StartTime.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(97.0 / 24, maskByTime.EndTime.LiteralAsDouble()!.Value, precision: 6);

        live.AssertAllLive(maskNode, maskByTime);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A zero-length window is no retake: no mask ladder, and the stage keeps the schedule its
    /// authored control asks for. That start step is the load-bearing half — a leaked retake forces
    /// it to 0, which is why the stage cannot be authored at control 1.0 here.
    /// </summary>
    [Fact]
    public async Task Retake_disabled_when_length_zero_leaves_graph_unchanged()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = RetakeClip(
            new JObject
            {
                ["startSeconds"] = 1.0,
                ["lengthSeconds"] = 0.0,
                ["strength"] = 1.0,
            },
            fixture.Stage(control: 0.5, steps: 10));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post => post["text2videoframes"] = Ltx2WorkflowFixture.RetakeClipFrames);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVSetAudioVideoMaskByTimeNode>());

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        // floor(10 x (1 - 0.5)); 0 is the codegen default, so only a non-zero value proves this.
        AssertShippedLiteral(workflow, sampler, "start_at_step", 5);
        // Positive control: the footage really is the clip's source, so a retake could have applied.
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        live.AssertAllLive(sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A retake window authored on a clip with no source video never activates — there is no
    /// footage to hold frozen. The stage still generates from noise.
    /// </summary>
    [Fact]
    public async Task Retake_ignored_when_clip_is_not_init_video()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(control: 0.5, steps: 10));
        clip["retake"] = new JObject
        {
            ["startSeconds"] = 1.0,
            ["lengthSeconds"] = 1.0,
            ["strength"] = 0.8,
        };

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        // Positive control: start_at_step cannot serve — the parser forces stage 0 of a non-sourced
        // clip to control 1.0, so it is 0 for the same reason an active retake would make it 0.
        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(10, sampler.Steps.LiteralAsInt());

        live.AssertAllLive(sampler);
        AssertShippable(bridge, workflow, live);
    }
}
