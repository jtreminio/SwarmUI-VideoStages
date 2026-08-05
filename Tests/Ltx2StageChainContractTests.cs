using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class Ltx2StageChainContractTests
{
    private const int UpscaledEdge = 1024;

    private static ImageScaleNode FramingOf(LTXVImgToVideoInplaceNode guide) =>
        Assert.IsType<ImageScaleNode>(
            Assert.IsType<LTXVPreprocessNode>(guide.Image.Connection?.Node).Image.Connection?.Node);

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
        Assert.Equal(1.0, guide.Strength.LiteralAsDouble());
        Assert.Equal(false, guide.Bypass.LiteralValue);
        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(guide, JointLatentOf(sampler).VideoLatent.Connection?.Node);
        Assert.Same(BaseImage(bridge), FramingOf(guide).Image.Connection?.Node);

        live.AssertAllLive(guide, sampler);
        AssertShippable(bridge, workflow, live);
    }

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
            Assert.Empty(bridge.Graph.NodesOfType<LTXVAddGuideNode>());
            Assert.Empty(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        }

        live.AssertAllLive(sampler);
        AssertShippable(bridge, workflow, live);
    }

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
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

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

        // One split per AV latent keeps video and audio decodes paired.
        IReadOnlyList<LTXVSeparateAVLatentNode> splits =
            [.. bridge.Graph.NodesOfType<LTXVSeparateAVLatentNode>()];
        Assert.Equal(
            splits.Count,
            splits.Select(split => split.AvLatent.Connection).Distinct().Count());

        live.AssertAllLive(scaler, guide, first, second);
        AssertShippable(bridge, workflow, live);
    }

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

        // The framing scale proves the requested 2048 pixel scale is absent.
        Assert.Equal(UpscaledEdge, FramingOf(lastGuide).Width.LiteralAsInt());
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() > UpscaledEdge
                || scale.Height.LiteralAsInt() > UpscaledEdge);

        live.AssertAllLive(lastGuide, upscaling, last);
        AssertShippable(bridge, workflow, live);
    }

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
        LTXVImgToVideoInplaceNode fallbackGuide = Assert.IsType<LTXVImgToVideoInplaceNode>(
            secondLatent.VideoLatent.Connection?.Node);
        Assert.Same(reEncode, fallbackGuide.LatentInput.Connection?.Node);

        live.AssertAllLive(first, second, reEncode, fallbackGuide);
        AssertShippable(result, workflow, live);
    }

    [Fact]
    public async Task Each_clip_decodes_only_at_its_terminal_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject first = MakeClip(1.0, fixture.Stage(control: 0.5), fixture.Stage(control: 0.5));
        first["boundaryOut"] = Constants.BoundaryOutCut;
        JObject second = MakeClip(1.0, fixture.Stage(control: 0.5), fixture.Stage(control: 0.5));
        int[] audioDecodeCountsBeforeCleanup = null;
        WorkflowGenerator.WorkflowGenStep inspectDedicatedBranches = new(g =>
        {
            using WorkflowBridge current = WorkflowBridge.Create(g.Workflow);
            audioDecodeCountsBeforeCleanup = new[] { 1, 3 }
                .Select(stageId => OutputOf(current, StageSampler(current, stageId)))
                .Select(output => current.Graph.NodesOfType<LTXVAudioVAEDecodeNode>()
                    .Count(decode => ReferenceEquals(
                        decode.Samples.Connection,
                        output.AudioLatent)))
                .ToArray();
        }, Constants.WorkflowStepPriority.RunConfiguredStages + 0.01);

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(first, second)),
            extraSteps: [inspectDedicatedBranches]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal([1, 1], audioDecodeCountsBeforeCleanup);
        SwarmKSamplerNode[] stages =
            [.. Enumerable.Range(0, 4).Select(id => StageSampler(bridge, id))];

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
        IReadOnlyList<LTXVAudioVAEDecodeNode> audioDecodes =
            [.. bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>()];
        Assert.Equal(2, audioDecodes.Count);
        foreach (VAEDecodeTiledNode decode in decodes)
        {
            LTXVSeparateAVLatentNode output = Assert.IsType<LTXVSeparateAVLatentNode>(
                decode.Samples.Connection?.Node);
            Assert.Single(
                audioDecodes,
                audioDecode => ReferenceEquals(
                    audioDecode.Samples.Connection,
                    output.AudioLatent));
        }

        BatchImagesNodeNode batch = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Same(batch, live.FinalVideoSave().Images.Connection?.Node);

        live.AssertAllLive([.. stages, batch]);
        AssertShippable(bridge, workflow, live);
    }

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

        Assert.Same(
            Assert.Single(bridge.Graph.NodesOfType<LTXVConditioningNode>()),
            first.Positive.Connection?.Node);
        Assert.Same(first.Positive.Connection?.Node, second.Positive.Connection?.Node);

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
        Assert.True(ReachesUpstream(bridge, guideImage, BaseImage(bridge).Id));
        Assert.Equal(
            expectedPreprocessNodes,
            bridge.Graph.NodesOfType<LTXVPreprocessNode>().Count);

        Assert.Same(OutputOf(bridge, first).VideoLatent, secondGuide.LatentInput.Connection);

        live.AssertAllLive(first, second, secondGuide);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_generated_reference_keeps_the_host_vae_out_of_the_ltx_stage_chain()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(
            fixture.Stage(control: 0.5),
            fixture.Stage("PreviousStage", control: 0.5));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        VAELoaderNode ltxVae = Assert.Single(bridge.Graph.NodesOfType<VAELoaderNode>());
        CheckpointLoaderSimpleNode hostCheckpoint = Assert.Single(
            bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());
        // The host and LTX VAEs are deliberately distinct.
        Assert.Same(hostCheckpoint, BaseImage(bridge).Vae.Connection?.Node);

        Assert.Equal(hostCheckpoint.Id, $"{new StageRefStore(generator).Generated.Vae.Path[0]}");
        Assert.All(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>(),
            guide => Assert.Same(ltxVae, guide.Vae.Connection?.Node));
        Assert.All(
            bridge.Graph.NodesOfType<VAEDecodeTiledNode>(),
            decode => Assert.Same(ltxVae, decode.Vae.Connection?.Node));
        Assert.Equal(ltxVae.Id, $"{generator.CurrentVae.Path[0]}");

        live.AssertAllLive(ltxVae, StageSampler(bridge, 0), StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }


    private static WorkflowGenerator.WorkflowGenStep PublishAceStepFunAudioTrackStep(int track) =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(track));
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput);

    private static void AssertNoStageFeedsItself(WorkflowBridge bridge)
    {
        Assert.All(bridge.Graph.NodesOfType<SwarmKSamplerNode>(), sampler => Assert.False(
            bridge.Graph.IsReachableUpstream(sampler, sampler.Id),
            $"Sampler {sampler.Id} is reachable from its own inputs — a stage feeds its output "
                + "back into itself."));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_chained_pixel_upscale_measures_audio_length_without_closing_a_cycle(
        int stageCount)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject[] stages =
        [
            fixture.Stage(control: 0.5, steps: 8),
            .. Enumerable.Range(1, stageCount - 2)
                .Select(_ => fixture.Stage("PreviousStage", control: 0.5, steps: 8)),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5, steps: 8),
        ];
        JObject clip = MakeClip(stages);
        clip["audioSource"] = "audio0";
        clip["clipLengthFromAudio"] = true;

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.ImageToVideoPost(MakeDocument(clip)),
            extraSteps: [PublishAceStepFunAudioTrackStep(0)]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        VAEDecodeAudioNode track = Assert.Single(bridge.Graph.NodesOfType<VAEDecodeAudioNode>());
        SwarmAudioLengthToFramesNode length = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Same(track, length.AudioInput.Connection?.Node);

        // The uncropped 1.5x scale creates the cycle-prone pixel path.
        Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() == 768
                && scale.Crop.LiteralAsString() == "disabled");
        AssertNoStageFeedsItself(bridge);

        live.AssertAllLive(track, length, StageSampler(bridge, stageCount - 1));
        AssertShippable(bridge, workflow, live);
    }


    private static WorkflowGenerator.WorkflowGenStep PublishBase2EditImageStep(int editStageIndex) =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            SwarmLoadImageB64Node published = bridge.AddNode(
                new SwarmLoadImageB64Node().With(ImageBase64: "data:image/png;base64,QUJDREVGRw=="));

            JObject media = new()
            {
                ["path"] = new JArray(published.Id, 0),
                ["dataType"] = WGNodeData.DT_IMAGE,
                ["width"] = VideoStagesWorkflowFixture.Width,
                ["height"] = VideoStagesWorkflowFixture.Height,
            };
            if (g.CurrentCompat()?.ID is string compatId)
            {
                media["compatId"] = compatId;
            }
            JObject payload = new() { ["media"] = media };
            if (g.CurrentVae?.Path is JArray { Count: 2 } vaePath)
            {
                JObject vae = new()
                {
                    ["path"] = new JArray(vaePath[0], vaePath[1]),
                    ["dataType"] = WGNodeData.DT_VAE,
                };
                if (g.CurrentVae.Compat?.ID is string vaeCompatId)
                {
                    vae["compatId"] = vaeCompatId;
                }
                payload["vae"] = vae;
            }
            g.NodeHelpers[$"b2e.published.edit.{editStageIndex}"] = payload.ToString(Formatting.None);
        }, Constants.WorkflowStepPriority.ApplyRootAudioMaskDimensions);

    [Fact]
    public async Task Native_ltx_stage_can_use_base2edit_edit_stage_as_clip_ref_image()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = fixture.Stage(control: 0.5);
        stage.Remove("imageReference");
        stage["refStrengths"] = new JArray(0.35);
        JObject clip = MakeClipWithRefs([MakeRef("edit0", frame: 1)], stage);

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.ImageToVideoPost(MakeDocument(clip)),
            extraSteps: [PublishBase2EditImageStep(0)]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node published = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        LTXVImgToVideoInplaceNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Same(published, FramingOf(guide).Image.Connection?.Node);
        Assert.Equal(0.35, guide.Strength.LiteralAsDouble());

        SwarmKSamplerNode stageSampler = StageSampler(bridge, 0);
        Assert.Same(guide, JointLatentOf(stageSampler).VideoLatent.Connection?.Node);

        live.AssertAllLive(published, guide, stageSampler);
        AssertShippable(bridge, workflow, live);
    }

    [Theory]
    [InlineData("edit0", "Base2Edit stage 0 does not exist")]
    [InlineData("edit99", "Base2Edit stage 99 does not exist")]
    public async Task Missing_base2edit_stage_reference_warns_and_continues(
        string reference,
        string expectedWarning)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(fixture.Stage(reference, control: 0.5)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains(expectedWarning, StringComparison.Ordinal));
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }
}
