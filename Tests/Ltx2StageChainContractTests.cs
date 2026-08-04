using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Stage-to-stage chaining on LTX-2 — guide selection, latent handoff, upscale dispatch, audio
/// carry-over and the published tail — generated through the real Comfy API POST path.
/// <para>
/// Shape matters here. Under image-to-video, a clip with no <c>initVideo</c> has core's LTX root
/// pruned at 11.05 and the root reverts to the base image, which stage 0 injects as its guide
/// unless an authored ref suppresses it; under text-to-video there is no base image at all. Each
/// test picks the shape that makes its subject observable.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class Ltx2StageChainContractTests
{
    /// <summary>A latent-model upscale doubles 512 to this.</summary>
    private const int UpscaledEdge = 1024;

    /// <summary>The <c>ImageScale</c> that fits a guide image to the stage resolution.</summary>
    private static ImageScaleNode FramingOf(LTXVImgToVideoInplaceNode guide) =>
        Assert.IsType<ImageScaleNode>(
            Assert.IsType<LTXVPreprocessNode>(guide.Image.Connection?.Node).Image.Connection?.Node);

    /// <summary>
    /// Stage 0 of an image-to-video request injects the host base image as its guide. "Exactly
    /// once" is the contract: the reference resolves to media the stage is already building from,
    /// so a second preprocess/in-place pair on top of it would be pure waste.
    /// </summary>
    [Fact]
    public async Task Stage_zero_injects_the_host_base_image_as_its_guide_exactly_once()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, fixture.Stage(control: 0.5));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVPreprocessNode preprocess = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        LTXVImgToVideoInplaceNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Same(preprocess, guide.Image.Connection?.Node);
        Assert.Same(BaseImage(bridge), FramingOf(guide).Image.Connection?.Node);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(guide, JointLatentOf(sampler).VideoLatent.Connection?.Node);

        // The extension publishes its own save off a retargeted split, not off the raw sampler.
        LTXVSeparateAVLatentNode output = OutputOf(bridge, sampler);
        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        VAEDecodeTiledNode videoDecode = Assert.IsType<VAEDecodeTiledNode>(
            save.Images.Connection?.Node);
        Assert.Same(output, videoDecode.Samples.Connection?.Node);
        LTXVAudioVAEDecodeNode audioDecode = Assert.IsType<LTXVAudioVAEDecodeNode>(
            save.Audio.Connection?.Node);
        Assert.Same(output, audioDecode.Samples.Connection?.Node);
        Assert.Equal(1, audioDecode.Samples.Connection.SlotIndex);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(videoDecode.Id, $"{generator.CurrentMedia.Path[0]}");

        live.AssertAllLive(preprocess, guide, sampler, videoDecode, audioDecode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Two trimmers can reach this graph — core's own priority-10 step and the extension's
    /// <c>GlobalVideoFrameTrimmer</c> — so the count is the point: zero frames must produce no
    /// wrapper at all, and a real trim must produce one, not two stacked.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 1)]
    public async Task Frame_trims_wrap_the_published_video_at_most_once(
        int trimFrames,
        int expectedTrimNodes)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = MakeClip(1.0, fixture.Stage(control: 0.5));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post =>
            {
                post["trimvideostartframes"] = trimFrames;
                post["trimvideoendframes"] = trimFrames;
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        IReadOnlyList<SwarmTrimFramesNode> trims =
            [.. bridge.Graph.NodesOfType<SwarmTrimFramesNode>()];
        Assert.Equal(expectedTrimNodes, trims.Count);

        ComfyNode published = live.FinalVideoSave().Images.Connection?.Node;
        if (expectedTrimNodes == 0)
        {
            Assert.IsType<VAEDecodeTiledNode>(published);
        }
        else
        {
            SwarmTrimFramesNode trim = Assert.IsType<SwarmTrimFramesNode>(published);
            Assert.Equal(trimFrames, trim.TrimStart.LiteralAsInt());
            Assert.Equal(trimFrames, trim.TrimEnd.LiteralAsInt());
            Assert.IsType<VAEDecodeTiledNode>(trim.Image.Connection?.Node);
        }

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Final_decode_uses_the_requests_vae_tiling_overrides()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = MakeClip(1.0, fixture.Stage(control: 0.5));

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip),
            post =>
            {
                post["vaetilesize"] = 960;
                post["vaetileoverlap"] = 96;
                post["vaetemporaltilesize"] = 512;
                post["vaetemporaltileoverlap"] = 12;
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        VAEDecodeTiledNode decode = Assert.IsType<VAEDecodeTiledNode>(
            live.FinalVideoSave().Images.Connection?.Node);
        Assert.Equal(960, decode.TileSize.LiteralAsInt());
        Assert.Equal(96, decode.Overlap.LiteralAsInt());
        Assert.Equal(512, decode.TemporalSize.LiteralAsInt());
        Assert.Equal(12, decode.TemporalOverlap.LiteralAsInt());

        live.AssertAllLive(decode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A stage-level <c>Base</c> reference carries no strength of its own — <c>refStrengths</c>
    /// only indexes clip refs — so the guide runs at the architecture default of 1.0. That is also
    /// <c>LTXVImgToVideoInplace</c>'s codegen default and cannot be authored differently here, so
    /// the strength alone proves little; the wiring assertions are what make the test falsifiable.
    /// <see cref="Primary_ref_strength_scales_the_in_place_guide_and_zero_removes_it"/> is the
    /// control that an authored strength does reach this input.
    /// </summary>
    [Fact]
    public async Task Stage_guide_uses_the_core_default_strength_without_a_ref_override()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, fixture.Stage("Base", control: 0.5));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVImgToVideoInplaceNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        AssertShippedLiteral(workflow, guide, "strength", 1.0);
        Assert.Equal(false, ShippedInput(workflow, guide, "bypass"));
        // The strength is only worth anything if this guide is the one the stage samples.
        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(guide, JointLatentOf(sampler).VideoLatent.Connection?.Node);
        Assert.Same(BaseImage(bridge), FramingOf(guide).Image.Connection?.Node);

        live.AssertAllLive(guide, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A first-frame clip reference routes to the in-place branch, where its authored strength is
    /// the guide's strength and zero means "no guide": the stage samples the bare empty latent.
    /// </summary>
    [Theory]
    [InlineData(0.35, true)]
    [InlineData(0.0, false)]
    public async Task Primary_ref_strength_scales_the_in_place_guide_and_zero_removes_it(
        double strength,
        bool guided)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = fixture.Stage(control: 0.5);
        stage["refStrengths"] = new JArray(strength);
        JObject clip = MakeClipWithRefs(refs: [MakeRef("Base", frame: 1)], stage);
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        INodeOutput videoLatent = JointLatentOf(sampler).VideoLatent.Connection;
        if (guided)
        {
            LTXVImgToVideoInplaceNode guide = Assert.IsType<LTXVImgToVideoInplaceNode>(
                videoLatent?.Node);
            Assert.Equal(strength, guide.Strength.LiteralAsDouble());
            Assert.Same(BaseImage(bridge), FramingOf(guide).Image.Connection?.Node);
        }
        else
        {
            Assert.IsType<EmptyLTXVLatentVideoNode>(videoLatent?.Node);
            Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
            Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
            // A dropped ref must not fall back to the add-guide branch either; that would thread
            // conditioning into the sampler without touching the latent path asserted above.
            Assert.Empty(bridge.Graph.NodesOfType<LTXVAddGuideNode>());
            Assert.Empty(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        }

        live.AssertAllLive(sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A reference at a frame offset routes to <c>LTXVAddGuide</c> instead, which the crop node
    /// must later undo. Zero strength drops the pair.
    /// </summary>
    [Theory]
    [InlineData(0.55, 1)]
    [InlineData(0.0, 0)]
    public async Task Add_guide_and_its_crop_are_dropped_at_zero_ref_strength(
        double strength,
        int expectedGuides)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = fixture.Stage(control: 0.5);
        stage["refStrengths"] = new JArray(strength);
        JObject clip = MakeClipWithRefs(refs: [MakeRef("Base", frame: 2)], stage);
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(expectedGuides, bridge.Graph.NodesOfType<LTXVAddGuideNode>().Count);
        Assert.Equal(expectedGuides, bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count);
        // A frame offset never falls back to the in-place branch.
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Not a tautology: <c>LTXVAddGuide</c> takes conditioning as input and hands conditioning back
    /// out, so it could just as easily have been wired to the raw text encoders. Reading the
    /// <c>LTXVConditioning</c> outputs pins that the frame-rate conditioning runs first, and the
    /// sampler reading the guide's outputs pins that it runs after.
    /// </summary>
    [Fact]
    public async Task Add_guide_threads_conditioning_between_ltxv_conditioning_and_the_crop()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = fixture.Stage(control: 0.5);
        stage["refStrengths"] = new JArray(0.55);
        JObject clip = MakeClipWithRefs(refs: [MakeRef("Base", frame: 2)], stage);
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVConditioningNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConditioningNode>());
        LTXVAddGuideNode addGuide = Assert.Single(bridge.Graph.NodesOfType<LTXVAddGuideNode>());
        Assert.Same(conditioning.Positive, addGuide.PositiveInput.Connection);
        Assert.Same(conditioning.Negative, addGuide.NegativeInput.Connection);
        Assert.Equal(2, addGuide.FrameIdx.LiteralAsInt());
        Assert.Equal(0.55, addGuide.Strength.LiteralAsDouble());

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(addGuide.Positive, sampler.Positive.Connection);
        Assert.Same(addGuide.Negative, sampler.Negative.Connection);
        Assert.Same(addGuide.Latent, JointLatentOf(sampler).VideoLatent.Connection);

        // The guide frames are cropped back off before the video is decoded.
        LTXVCropGuidesNode crop = Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        Assert.Same(addGuide.Positive, crop.PositiveInput.Connection);
        Assert.Same(addGuide.Negative, crop.NegativeInput.Connection);
        Assert.Same(OutputOf(bridge, sampler).VideoLatent, crop.LatentInput.Connection);
        VAEDecodeTiledNode decode = Assert.IsType<VAEDecodeTiledNode>(
            live.FinalVideoSave().Images.Connection?.Node);
        Assert.Same(crop.Latent, decode.Samples.Connection);

        live.AssertAllLive(conditioning, addGuide, sampler, crop, decode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A plain latent scale is a pure latent-space handoff in both directions: the next stage
    /// consumes the scaled latent straight off the previous stage's split, with no pixel round
    /// trip and no latent-model upsampler.
    /// </summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    public async Task Latent_scale_hands_the_previous_stage_latent_over_without_a_pixel_round_trip(
        double scale)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, 
            fixture.Stage(control: 0.5),
            fixture.Stage("PreviousStage", control: 0.5, upscale: scale,
                upscaleMethod: "latent-bislerp"));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LatentUpscaleByNode scaler = Assert.Single(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Equal("bislerp", scaler.UpscaleMethod.LiteralAsString());
        Assert.Equal(scale, scaler.ScaleBy.LiteralAsDouble());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Same(OutputOf(bridge, first).VideoLatent, scaler.Samples.Connection);
        LTXVImgToVideoInplaceNode guide = Assert.IsType<LTXVImgToVideoInplaceNode>(
            JointLatentOf(second).VideoLatent.Connection?.Node);
        Assert.Same(scaler.LATENT, guide.LatentInput.Connection);

        // Each AV latent must be split exactly once, or the video and audio decodes end up reading
        // different separators. Core only collapses a split sitting over a concat, so two splits
        // over one sampler output would survive it — this is the extension's contract, not core's.
        IReadOnlyList<LTXVSeparateAVLatentNode> splits =
            [.. bridge.Graph.NodesOfType<LTXVSeparateAVLatentNode>()];
        Assert.Equal(
            splits.Count,
            splits.Select(split => split.AvLatent.Connection).Distinct().Count());

        live.AssertAllLive(scaler, guide, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// After a latent-model upscale the next stage refines the upscaled latent and takes its guide
    /// from a decode of that same latent, re-framed to the new resolution — not from the old one.
    /// </summary>
    [Fact]
    public async Task A_latent_model_upscale_carries_both_the_latent_and_the_guide_forward()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, 
            fixture.Stage(control: 0.5),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 2.0,
                upscaleMethod: LtxV23SpatialUpscaler),
            fixture.Stage("PreviousStage", control: 0.5));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode upscaling = StageSampler(bridge, 1);
        SwarmKSamplerNode last = StageSampler(bridge, 2);

        LTXVLatentUpsamplerNode upsampler = Assert.Single(
            bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());
        Assert.Same(OutputOf(bridge, first).VideoLatent, upsampler.Samples.Connection);
        Assert.Same(
            upsampler.LATENT,
            Assert.IsType<LTXVImgToVideoInplaceNode>(
                JointLatentOf(upscaling).VideoLatent.Connection?.Node).LatentInput.Connection);

        LTXVImgToVideoInplaceNode lastGuide = Assert.IsType<LTXVImgToVideoInplaceNode>(
            JointLatentOf(last).VideoLatent.Connection?.Node);
        Assert.Same(OutputOf(bridge, upscaling).VideoLatent, lastGuide.LatentInput.Connection);
        ImageScaleNode framing = FramingOf(lastGuide);
        Assert.Equal(UpscaledEdge, framing.Width.LiteralAsInt());
        Assert.Equal(UpscaledEdge, framing.Height.LiteralAsInt());
        Assert.Same(
            OutputOf(bridge, upscaling).VideoLatent,
            Assert.IsType<VAEDecodeTiledNode>(framing.Image.Connection?.Node).Samples.Connection);

        live.AssertAllLive(upsampler, lastGuide, first, upscaling, last);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A pixel upscale has no scaffolding on the latent path, so a stage that asks for one after a
    /// latent-model upscale keeps the resolution it inherited: the latent passes through untouched
    /// and nothing is scaled to 2048.
    /// </summary>
    [Fact]
    public async Task A_pixel_upscale_after_a_latent_model_upscale_is_ignored()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, 
            fixture.Stage(control: 0.5),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 2.0,
                upscaleMethod: LtxV23SpatialUpscaler),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 2.0,
                upscaleMethod: "pixel-lanczos"));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Single(bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());
        SwarmKSamplerNode upscaling = StageSampler(bridge, 1);
        SwarmKSamplerNode last = StageSampler(bridge, 2);

        LTXVImgToVideoInplaceNode lastGuide = Assert.IsType<LTXVImgToVideoInplaceNode>(
            JointLatentOf(last).VideoLatent.Connection?.Node);
        Assert.Same(OutputOf(bridge, upscaling).VideoLatent, lastGuide.LatentInput.Connection);

        // The framing scale is the positive control: it exists, and it sits at the resolution the
        // latent-model upscale produced rather than the 2048 the pixel upscale asked for.
        Assert.Equal(UpscaledEdge, FramingOf(lastGuide).Width.LiteralAsInt());
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() > UpscaledEdge
                || scale.Height.LiteralAsInt() > UpscaledEdge);

        live.AssertAllLive(lastGuide, upscaling, last);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A <c>Generated</c> reference on a later stage names the latent that stage is already
    /// refining, so the guide is redundant and must be skipped entirely — the joint latent is wired
    /// straight from the previous stage's split.
    /// </summary>
    [Fact]
    public async Task A_generated_reference_on_a_later_stage_skips_the_guide_entirely()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, fixture.Stage(control: 0.5), fixture.Stage(control: 0.5));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Only stage 0's host-base-image injection; stage 1 adds none of its own.
        Assert.Single(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        LTXVSeparateAVLatentNode firstOutput = OutputOf(bridge, first);
        LTXVConcatAVLatentNode secondLatent = JointLatentOf(second);
        Assert.Same(firstOutput.VideoLatent, secondLatent.VideoLatent.Connection);
        Assert.Same(firstOutput.AudioLatent, secondLatent.AudioLatent.Connection);

        VAEDecodeTiledNode decode = Assert.IsType<VAEDecodeTiledNode>(
            live.FinalVideoSave().Images.Connection?.Node);
        Assert.Same(OutputOf(bridge, second).VideoLatent, decode.Samples.Connection);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(decode.Id, $"{generator.CurrentMedia.Path[0]}");

        live.AssertAllLive(first, second, decode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The latent handoff between stages is prepared before the alt image-to-video pre-handlers
    /// run. A handler that re-points the current media at a pixel node invalidates it, and the next
    /// stage must fall back to re-encoding those pixels rather than reusing the stale latent.
    /// </summary>
    [Fact]
    public async Task A_prehandler_pixel_wrapper_invalidates_the_prepared_latent_handoff()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, fixture.Stage(control: 0.5), fixture.Stage(control: 0.5));

        string wrapperId = null;
        Action<WorkflowGenerator.ImageToVideoGenInfo> wrapSecondStageSource = info =>
        {
            if (info.ContextID != VideoStagesExtension.SectionIdForStage(1))
            {
                return;
            }
            WGNodeData priorMedia = info.Generator.CurrentMedia;
            using WorkflowBridge bridge = WorkflowBridge.Create(info.Generator.Workflow);
            ImageScaleNode wrapper = bridge.AddNode(new ImageScaleNode().With(
                Width: priorMedia.Width ?? VideoStagesWorkflowFixture.Width,
                Height: priorMedia.Height ?? VideoStagesWorkflowFixture.Height,
                UpscaleMethod: "lanczos",
                Crop: "disabled"));
            wrapper.Image.TryConnectFromPath(bridge, priorMedia.Path);
            WGNodeData wrappedMedia = priorMedia.WithPath(new JArray(wrapper.Id, 0));
            wrappedMedia.AttachedAudio = priorMedia.AttachedAudio;
            info.Generator.CurrentMedia = wrappedMedia;
            wrapperId = wrapper.Id;
        };

        JObject workflow;
        WorkflowGenerator.AltImageToVideoPreHandlers.Add(wrapSecondStageSource);
        try
        {
            workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        }
        finally
        {
            Assert.True(
                WorkflowGenerator.AltImageToVideoPreHandlers.Remove(wrapSecondStageSource));
        }
        using WorkflowBridge result = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(result);

        Assert.NotNull(wrapperId);
        SwarmKSamplerNode first = StageSampler(result, 0);
        SwarmKSamplerNode second = StageSampler(result, 1);
        LTXVConcatAVLatentNode secondLatent = JointLatentOf(second);
        Assert.True(ReachesUpstream(result, secondLatent, wrapperId));

        VAEEncodeNode reEncode = Assert.Single(
            result.Graph.NodesOfType<VAEEncodeNode>(),
            node => ReachesUpstream(result, node, wrapperId));
        Assert.True(ReachesUpstream(result, reEncode, first.Id));
        Assert.True(ReachesUpstream(result, secondLatent, reEncode.Id));

        live.AssertAllLive(first, second, reEncode);
        AssertShippable(result, workflow, live);
    }

    /// <summary>
    /// Stages inside a clip hand latents to each other; only the clip's last stage is decoded, and
    /// the clips meet as pixels at the batch node. A cut boundary keeps the second clip's chain
    /// independent of the first.
    /// </summary>
    [Fact]
    public async Task Each_clip_decodes_only_at_its_terminal_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = MakeClip(1.0, fixture.Stage(control: 0.5), fixture.Stage(control: 0.5));
        first["boundaryOut"] = Constants.BoundaryOutCut;
        JObject second = MakeClip(1.0, fixture.Stage(control: 0.5), fixture.Stage(control: 0.5));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode[] stages =
            [.. Enumerable.Range(0, 4).Select(id => StageSampler(bridge, id))];

        // Within a clip the handoff is the raw latent split — no decode, no re-encode.
        Assert.Same(
            OutputOf(bridge, stages[0]).VideoLatent,
            JointLatentOf(stages[1]).VideoLatent.Connection);
        Assert.Same(
            OutputOf(bridge, stages[2]).VideoLatent,
            JointLatentOf(stages[3]).VideoLatent.Connection);
        Assert.False(ReachesUpstream(bridge, JointLatentOf(stages[2]), stages[0].Id));

        IReadOnlyList<VAEDecodeTiledNode> decodes =
            [.. bridge.Graph.NodesOfType<VAEDecodeTiledNode>()];
        Assert.Equal(2, decodes.Count);
        Assert.Contains(
            decodes,
            decode => ReferenceEquals(
                decode.Samples.Connection, OutputOf(bridge, stages[1]).VideoLatent));
        Assert.Contains(
            decodes,
            decode => ReferenceEquals(
                decode.Samples.Connection, OutputOf(bridge, stages[3]).VideoLatent));

        BatchImagesNodeNode batch = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Same(batch, live.FinalVideoSave().Images.Connection?.Node);

        live.AssertAllLive([.. stages, batch]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// <c>PreviousStage</c> and <c>Stage0</c> name the same stage for stage 1, so the two must
    /// build the same graph. Audio meanwhile advances stage by stage: stage 0 starts from an empty
    /// audio latent and stage 1 continues from what stage 0 produced.
    /// </summary>
    [Theory]
    [InlineData("PreviousStage")]
    [InlineData("Stage0")]
    public async Task Stage_reference_aliases_resolve_alike_and_audio_advances_per_stage(
        string secondStageReference)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, 
            fixture.Stage("Base"),
            fixture.Stage(secondStageReference));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        LTXVSeparateAVLatentNode firstOutput = OutputOf(bridge, first);

        // Each stage gets conditioning of its own, so a shared guide is not a shared prompt.
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVConditioningNode>().Count);
        Assert.NotSame(first.Positive.Connection?.Node, second.Positive.Connection?.Node);

        LTXVImgToVideoInplaceNode secondGuide = Assert.IsType<LTXVImgToVideoInplaceNode>(
            JointLatentOf(second).VideoLatent.Connection?.Node);
        Assert.Same(firstOutput.VideoLatent, secondGuide.LatentInput.Connection);
        Assert.Same(
            firstOutput.VideoLatent,
            Assert.IsType<VAEDecodeTiledNode>(FramingOf(secondGuide).Image.Connection?.Node)
                .Samples.Connection);

        Assert.IsType<LTXVEmptyLatentAudioNode>(
            JointLatentOf(first).AudioLatent.Connection?.Node);
        Assert.Same(firstOutput.AudioLatent, JointLatentOf(second).AudioLatent.Connection);

        LTXVSeparateAVLatentNode secondOutput = OutputOf(bridge, second);
        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        Assert.Same(
            secondOutput.VideoLatent,
            Assert.IsType<VAEDecodeTiledNode>(save.Images.Connection?.Node).Samples.Connection);
        Assert.Same(
            secondOutput.AudioLatent,
            Assert.IsType<LTXVAudioVAEDecodeNode>(save.Audio.Connection?.Node).Samples.Connection);

        live.AssertAllLive(first, second, secondGuide);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Audio normally advances with the video, each stage re-sampling what the last produced.
    /// <c>reuseAudio</c> freezes it after the first stage instead, so every later stage conditions
    /// on the same audio track. Three stages are the minimum that tells the two apart.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reuse_audio_pins_every_later_stage_to_the_first_stages_audio(bool reuseAudio)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, 
            fixture.Stage("Base"),
            fixture.Stage("PreviousStage"),
            fixture.Stage("PreviousStage"));
        clip["reuseAudio"] = reuseAudio;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode[] stages =
            [.. Enumerable.Range(0, 3).Select(id => StageSampler(bridge, id))];

        Assert.IsType<LTXVEmptyLatentAudioNode>(
            JointLatentOf(stages[0]).AudioLatent.Connection?.Node);
        Assert.Same(
            OutputOf(bridge, stages[0]).AudioLatent,
            JointLatentOf(stages[1]).AudioLatent.Connection);
        Assert.Same(
            OutputOf(bridge, stages[reuseAudio ? 0 : 1]).AudioLatent,
            JointLatentOf(stages[2]).AudioLatent.Connection);

        live.AssertAllLive(stages);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// After stage 0 runs, the live generated media has moved on to stage 0's output. An authored
    /// <c>Base</c> reference on stage 1 must not follow it — it stays on the host base image, and
    /// because stage 0's guide resolved to that same image the preprocess is deduplicated rather
    /// than rebuilt. <c>PreviousStage</c> is the control that does follow the live media.
    /// </summary>
    [Theory]
    [InlineData("Base", false, 1)]
    [InlineData("PreviousStage", true, 2)]
    public async Task An_authored_base_reference_does_not_follow_the_live_generated_media(
        string secondStageReference,
        bool followsLiveMedia,
        int expectedPreprocessNodes)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(1.0, 
            fixture.Stage(control: 0.5),
            fixture.Stage(secondStageReference, control: 0.5));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        LTXVImgToVideoInplaceNode secondGuide = Assert.IsType<LTXVImgToVideoInplaceNode>(
            JointLatentOf(second).VideoLatent.Connection?.Node);
        ComfyNode guideImage = secondGuide.Image.Connection?.Node;
        Assert.NotNull(guideImage);

        Assert.Equal(followsLiveMedia, ReachesUpstream(bridge, guideImage, first.Id));
        // Both arms trace to the base image; only the live-media one goes through stage 0 to reach it.
        Assert.True(ReachesUpstream(bridge, guideImage, BaseImage(bridge).Id));
        Assert.Equal(
            expectedPreprocessNodes,
            bridge.Graph.NodesOfType<LTXVPreprocessNode>().Count);

        // Whichever guide it picks, stage 1 still refines stage 0's latent.
        Assert.Same(OutputOf(bridge, first).VideoLatent, secondGuide.LatentInput.Connection);

        live.AssertAllLive(first, second, secondGuide);
        AssertShippable(bridge, workflow, live);
    }
}
