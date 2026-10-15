using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Authoring;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;
using VideoStages.Architectures.Ltx2.Runtime;

namespace VideoStages.Tests;

// Image-to-video keeps the base pass live through stage 0’s guide. Text-to-video prunes the replaced host root, including ControlNet nodes outside core’s cleanup list.
[Collection("VideoStagesTests")]
public class Ltx2CoreVideoContractTests
{
    /// <summary>Long enough to clear <c>ValidateParam</c>'s 10-character floor on IMAGE payloads.</summary>
    private const string VideoPayload = "data:video/mp4;base64,AAAAAAAAAAAAAAAAAAAAAA==";

    private const string LtxV15SpatialUpscaler =
        "latentmodel-ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors";

    private const string PreprocessorClass = "UnitTestPreprocessor";

    private const string DiffpatchControlNet = "UnitTest_ControlNetDiffpatch";

    private const string AliMamaControlNet = "UnitTest_ControlNetAliMama";

    /// <summary>A 1x1 PNG, for the init/mask pair core needs before it will build an AliMama
    /// apply.</summary>
    private const string ImagePayload =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ"
        + "AAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static void RequestControlNet(JObject post)
    {
        post["controlnetstrength"] = 0.8;
        post["controlnetmodel"] = "UnitTest_ControlNet";
        post["controlnetpreprocessor"] = PreprocessorClass;
        post["controlnetimageinput"] = VideoPayload;
    }

    private static void RequestControlNetTwo(JObject post)
    {
        post["controlnettwostrength"] = 0.8;
        post["controlnettwomodel"] = "UnitTest_ControlNet";
        post["controlnettwopreprocessor"] = PreprocessorClass;
        post["controlnettwoimageinput"] = VideoPayload;
    }

    private static Ltx2WorkflowFixture WithControlNetStubs(Ltx2WorkflowFixture fixture)
    {
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        fixture.InstallModel("ControlNet", "UnitTest_ControlNet.safetensors");
        fixture.InstallModel("ControlNet", $"{DiffpatchControlNet}.safetensors");
        fixture.InstallModel("ControlNet", $"{AliMamaControlNet}.safetensors");
        fixture.InstallModel("LoRA", "UnitTest_ControlNetLora.safetensors");
        return fixture;
    }

    // Core selects the apply branch by ModelClass.ID; stubs have no class by default.
    private static void ClassifyControlNet(string name, string modelClassId) =>
        Program.T2IModelSets["ControlNet"].Models[$"{name}.safetensors"].ModelClass =
            new T2IModelClass { ID = modelClassId, Name = modelClassId };

    // Core selects AliMama from metadata normally populated by the model scan.
    private static void SetControlNetArchitecture(string name, string modelClassType) =>
        Program.T2IModelSets["ControlNet"].Models[$"{name}.safetensors"].Metadata =
            new T2IModelHandler.ModelMetadataStore { ModelClassType = modelClassType };

    private static JObject IcLoraClip(string driveSource, params JObject[] stages)
    {
        JObject clip = MakeClip(1.0, stages);
        clip["icLoras"] = new JArray(new JObject
        {
            ["lora"] = "UnitTest_ControlNetLora",
            ["driveSource"] = driveSource,
            ["driveData"] = $"{IcLoraDriveData.Visual}",
        });
        return clip;
    }

    private static JObject IcLoraClip(params JObject[] stages) =>
        IcLoraClip(MediaSource.ControlNetOne, stages);

    // Core's preprocessor uses multiples of 8; LTX's resize uses 64.
    private static ResizeImageMaskNodeNode ResizeToMultiple(WorkflowBridge bridge, int multiple) =>
        Assert.Single(
            bridge.Graph.NodesOfType<ResizeImageMaskNodeNode>(),
            node => node.ExtraInputs?["resize_type.multiple"]?.Value<int>() == multiple);

    private static ResizeImageMaskNodeNode GuideResizeOverCorePreprocessor(WorkflowBridge bridge)
    {
        ComfyNode preprocessor = Assert.Single(bridge.Graph.NodesOfType(PreprocessorClass));
        ResizeImageMaskNodeNode coreResize = ResizeToMultiple(bridge, 8);
        Assert.Equal("scale to multiple", coreResize.ResizeType.LiteralAsString());
        Assert.Same(preprocessor, coreResize.Input.Connection?.Node);
        ResizeImageMaskNodeNode guideResize = ResizeToMultiple(bridge, 64);
        Assert.Same(coreResize.Resized, guideResize.Input.Connection);
        return guideResize;
    }

    private static LTXVAudioVAEEncodeNode EncodeOfCapturedControlNetAudio(WorkflowBridge bridge)
    {
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        LTXVAudioVAEEncodeNode encode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        SwarmEnsureAudioNode ensure = Assert.IsType<SwarmEnsureAudioNode>(
            encode.Audio.Connection?.Node);
        // Slot 1 is the audio stream; slot 0 is the frames the ControlNet apply takes.
        Assert.Same(components.Audio, ensure.Audio.Connection);
        return encode;
    }

    // Apply node types differ by model; their image inputs share this unwrap.
    private static ImageFromBatchNode AssertCoreUnwrapsFirstFrame(
        JObject workflow,
        ResizeImageMaskNodeNode guideResize,
        INodeInput applyImage)
    {
        ImageFromBatchNode firstFrame = Assert.IsType<ImageFromBatchNode>(
            applyImage.Connection?.Node);
        Assert.Same(guideResize.Resized, firstFrame.Image.Connection);
        AssertShippedLiteral(workflow, firstFrame, "batch_index", 0);
        AssertShippedLiteral(workflow, firstFrame, "length", 1);
        return firstFrame;
    }

    private static ImageFromBatchNode GuideFramesOf(WorkflowBridge bridge) =>
        Assert.IsType<ImageFromBatchNode>(
            Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>())
                .Image.Connection?.Node);

    private static ImageScaleNode FramingOf(LTXVPreprocessNode preprocess) =>
        Assert.IsType<ImageScaleNode>(PreprocessSource(preprocess).Node);

    private static SwarmSaveAnimationWSNode AssertPublishesGeneratorMedia(
        WorkflowBridge bridge,
        WorkflowGenerator generator)
    {
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(
            JToken.DeepEquals(
                WorkflowBridge.ToPath(save.Images.Connection!),
                generator.CurrentMedia.Path),
            "The request's published media is not what the single video save reads.");
        return save;
    }

    [Fact]
    public async Task A_stage_model_generates_video_even_when_the_request_asks_only_for_an_image()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject post = fixture.Post(MakeDocument(MakeClip(1.0, fixture.Stage(control: 0.5))));
        post["model"] = fixture.BaseModel.Name;
        post.Remove("text2videoframes");

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(post);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode baseSampler = fixture.BaseSampler(bridge);
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.IsType<CheckpointLoaderSimpleNode>(baseSampler.Model.Connection?.Node);
        Assert.IsType<UNETLoaderNode>(stage.Model.Connection?.Node);

        Assert.Single(bridge.Graph.NodesOfType<SwarmSaveImageWSNode>());
        AssertPublishesGeneratorMedia(bridge, generator);

        live.AssertAllLive(baseSampler, stage);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_single_stage_publishes_one_video_save_and_owns_the_requests_media()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(1.0, fixture.Stage(control: 0.5, steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(10, stage.Steps.LiteralAsInt());
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        SwarmSaveAnimationWSNode save = AssertPublishesGeneratorMedia(bridge, generator);

        live.AssertAllLive(stage, save);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Two_stages_publish_one_video_save_and_carry_their_own_sampler_settings()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject second = fixture.Stage("PreviousStage", control: 0.5, steps: 14, cfgScale: 6.0);
        second["sampler"] = "dpmpp_2m";
        second["scheduler"] = "karras";

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(1.0, fixture.Stage(control: 0.5, steps: 10), second))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode last = StageSampler(bridge, 1);
        Assert.Equal(10, first.Steps.LiteralAsInt());
        Assert.Equal(14, last.Steps.LiteralAsInt());
        Assert.Equal(6.0, last.Cfg.LiteralAsDouble());
        Assert.Equal("dpmpp_2m", last.SamplerName.LiteralAsString());
        Assert.Equal("karras", last.Scheduler.LiteralAsString());
        AssertPublishesGeneratorMedia(bridge, generator);

        live.AssertAllLive(first, last);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Stage_zero_replaces_cores_video_root_rather_than_adding_to_it()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(MakeClip(1.0, fixture.Stage("Base", control: 0.5, steps: 10))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Core uses the same Seed + 42; StageSampler rejects duplicate samplers.
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        LTXVPreprocessNode preprocess = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        LTXVImgToVideoInplaceNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Same(preprocess.OutputImage, guide.Image.Connection);
        Assert.Same(guide, JointLatentOf(stage).VideoLatent.Connection?.Node);

        live.AssertAllLive(preprocess, guide, stage);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Root_stage_handoff_leaves_nothing_orphaned_around_cores_video_save()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClipWithRefs(
            [MakeRef("Base"), MakeRef("Base", frame: 2)],
            fixture.Stage(control: 0.5, steps: 8)));
        document["width"] = 1280;
        document["height"] = 1024;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(1280, generator.CurrentMedia.Width);
        Assert.Equal(1024, generator.CurrentMedia.Height);

        // Both refs resolve to the same host base image, so one preprocess serves the frame-1
        // in-place guide and the frame-2 add-guide instead of each framing its own copy.
        LTXVPreprocessNode preprocess = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Same(
            preprocess.OutputImage,
            Assert.Single(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>())
                .Image.Connection);
        Assert.Same(
            preprocess.OutputImage,
            Assert.Single(bridge.Graph.NodesOfType<LTXVAddGuideNode>()).Image.Connection);

        live.AssertAllLive(preprocess, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Root_stage_resolution_frames_the_host_base_image_for_the_timeline()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(1.0, fixture.Stage(control: 0.5, steps: 10)));
        document["width"] = 1472;
        document["height"] = 832;

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ImageScaleNode framing = Assert.Single(bridge.Graph.NodesOfType<ImageScaleNode>());
        Assert.Same(BaseImage(bridge), framing.Image.Connection?.Node);
        // The POST asked for 512x512, so neither number here can be a coincidence.
        Assert.Equal(1472, framing.Width.LiteralAsInt());
        Assert.Equal(832, framing.Height.LiteralAsInt());
        Assert.Equal("center", framing.Crop.LiteralAsString());

        live.AssertAllLive(framing, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Root_stage_resolution_sizes_the_empty_video_latent()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(1.0, fixture.Stage(control: 0.5, steps: 10)));
        document["width"] = 768;
        document["height"] = 448;

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        EmptyLTXVLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(768, latent.Width.LiteralAsInt());
        Assert.Equal(448, latent.Height.LiteralAsInt());

        live.AssertAllLive(latent, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Root_stage_resolution_resizes_the_attached_audio_noise_mask()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(1.0, fixture.Stage(control: 0.5, steps: 10)));
        document["width"] = 384;
        document["height"] = 640;

        JObject workflow = await fixture.GenerateImageToVideoAsync(document, post =>
        {
            post["initimage"] = VideoPayload;
            post["width"] = 768;
            post["height"] = 1280;
        });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SetLatentNoiseMaskNode mask = Assert.Single(
            bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        SolidMaskNode solid = Assert.IsType<SolidMaskNode>(mask.Mask.Connection?.Node);
        Assert.Equal(384, solid.Width.LiteralAsInt());
        Assert.Equal(640, solid.Height.LiteralAsInt());
        Assert.Same(
            mask.LATENT,
            JointLatentOf(StageSampler(bridge, 0)).AudioLatent.Connection);

        // Positive control: the request really did ask for 768x1280, and core still frames its own
        // init image at that size — only the timeline's mask moved.
        Assert.Contains(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() == 768 && scale.Height.LiteralAsInt() == 1280);

        live.AssertAllLive(solid, mask, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Document_fps_replaces_the_requests_video_fps()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(1.0, fixture.Stage(control: 0.5, steps: 10)));
        document["fps"] = 30;

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVEmptyLatentAudioNode audio = Assert.Single(
            bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>());
        Assert.Equal(30, audio.FrameRate.LiteralAsInt());
        Assert.Equal(
            30,
            Assert.Single(bridge.Graph.NodesOfType<LTXVConditioningNode>())
                .FrameRate.LiteralAsInt());
        Assert.Equal(30, live.FinalVideoSave().Fps.LiteralAsInt());
        Assert.Equal(
            33,
            Assert.Single(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>())
                .Length.LiteralAsInt());

        live.AssertAllLive(audio, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Clip_duration_replaces_the_requested_video_frame_count()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(MakeClip(30.0, fixture.Stage(control: 0.5, steps: 8))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        EmptyLTXVLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(721, latent.Length.LiteralAsInt());

        live.AssertAllLive(latent, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task An_off_grid_frame_request_without_a_duration_is_preserved()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(fixture.Stage(control: 0.5, steps: 8))),
                    post =>
                    {
                        post["videoframes"] = 16;
                        post["steps"] = 19;
                    }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        EmptyLTXVLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(16, latent.Length.LiteralAsInt());
        Assert.Equal(8, StageSampler(bridge, 0).Steps.LiteralAsInt());
        Assert.Equal(16, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());

        live.AssertAllLive(latent, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Text_to_video_clip_duration_replaces_the_requested_frame_count()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject document = MakeDocument(MakeClip(10.0, fixture.Stage(steps: 8)));
        document["fps"] = 24;

        JObject workflow = await fixture.GenerateAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(
            241,
            Assert.Single(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>())
                .Length.LiteralAsInt());
        Assert.Equal(
            241,
            Assert.Single(bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>())
                .FramesNumber.LiteralAsInt());

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Text_to_video_preserves_an_off_grid_frame_request()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(
                    MakeDocument(MakeClip(fixture.Stage(steps: 8))),
                    post => post["text2videoframes"] = 16));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(
            16,
            Assert.Single(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>())
                .Length.LiteralAsInt());
        Assert.Equal(
            16,
            Assert.Single(bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>())
                .FramesNumber.LiteralAsInt());
        Assert.Equal(16, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Implicit_guides_are_built_only_for_the_opening_stage_of_a_chain()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject[] stages = [.. Enumerable.Range(0, 3).Select(_ =>
        {
            JObject stage = fixture.Stage(control: 0.5, steps: 8);
            stage.Remove("imageReference");
            return stage;
        })];
        JObject document = MakeDocument(MakeClip(1.0, stages));
        document["width"] = 1024;
        document["height"] = 1024;

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVPreprocessNode preprocess = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        LTXVImgToVideoInplaceNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Same(preprocess.OutputImage, guide.Image.Connection);
        ImageScaleNode framing = FramingOf(preprocess);
        Assert.Same(BaseImage(bridge), framing.Image.Connection?.Node);
        Assert.Equal(1024, framing.Width.LiteralAsInt());
        Assert.Equal(1024, framing.Height.LiteralAsInt());
        Assert.Equal("center", framing.Crop.LiteralAsString());

        SwarmKSamplerNode[] samplers =
            [.. Enumerable.Range(0, 3).Select(id => StageSampler(bridge, id))];
        Assert.Same(guide, JointLatentOf(samplers[0]).VideoLatent.Connection?.Node);
        for (int stage = 1; stage < samplers.Length; stage++)
        {
            LTXVSeparateAVLatentNode previous = OutputOf(bridge, samplers[stage - 1]);
            LTXVConcatAVLatentNode joint = JointLatentOf(samplers[stage]);
            Assert.Same(previous.VideoLatent, joint.VideoLatent.Connection);
            Assert.Same(previous.AudioLatent, joint.AudioLatent.Connection);
        }

        LTXVSeparateAVLatentNode last = OutputOf(bridge, samplers[^1]);
        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        Assert.Same(
            last.VideoLatent,
            Assert.IsType<VAEDecodeTiledNode>(save.Images.Connection?.Node).Samples.Connection);
        Assert.Same(
            last.AudioLatent,
            Assert.IsType<LTXVAudioVAEDecodeNode>(live.PublishedAudio()).Samples.Connection);

        live.AssertAllLive([.. samplers, preprocess, guide]);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_stage_without_frame_refs_guides_from_the_framed_base_image_at_full_strength()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(MakeClip(1.0, fixture.Stage(control: 0.5, steps: 10))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVPreprocessNode preprocess = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Same(BaseImage(bridge), FramingOf(preprocess).Image.Connection?.Node);
        LTXVImgToVideoInplaceNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        // Authored non-default strengths are covered by A_frame_one_base_ref_reuses_the_root_framing_at_its_authored_strength.
        Assert.Equal(1.0, guide.Strength.LiteralAsDouble());
        Assert.Same(preprocess.OutputImage, guide.Image.Connection);

        live.AssertAllLive(preprocess, guide, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Theory]
    [InlineData(null, null, 512, 512)]
    [InlineData(768, 448, 768, 448)]
    public async Task A_frame_one_base_ref_reuses_the_root_framing_at_its_authored_strength(
        int? rootWidth,
        int? rootHeight,
        int expectedWidth,
        int expectedHeight)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = fixture.Stage(control: 0.5, steps: 10);
        stage["keyframeStrengths"] = new JArray(0.55);
        JObject clip = MakeClipWithRefs(refs: [MakeRef("Base", frame: 1)], stage);
        clip["duration"] = 1.0;
        JObject document = MakeDocument(clip);
        if (rootWidth is not null)
        {
            document["width"] = rootWidth.Value;
            document["height"] = rootHeight.Value;
        }

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ImageScaleNode framing = Assert.Single(bridge.Graph.NodesOfType<ImageScaleNode>());
        Assert.Same(BaseImage(bridge), framing.Image.Connection?.Node);
        Assert.Equal(expectedWidth, framing.Width.LiteralAsInt());
        Assert.Equal(expectedHeight, framing.Height.LiteralAsInt());

        LTXVPreprocessNode preprocess = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Same(framing.IMAGE, PreprocessSource(preprocess));
        LTXVImgToVideoInplaceNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Equal(0.55, guide.Strength.LiteralAsDouble());
        Assert.Same(preprocess.OutputImage, guide.Image.Connection);

        live.AssertAllLive(framing, preprocess, guide, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_frame_two_base_ref_swaps_the_in_place_guide_for_an_add_guide_and_crop()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = fixture.Stage(control: 0.5, steps: 10);
        stage["keyframeStrengths"] = new JArray(0.55);
        JObject clip = MakeClipWithRefs(refs: [MakeRef("Base", frame: 2)], stage);
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        LTXVPreprocessNode preprocess = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        LTXVAddGuideNode addGuide = Assert.Single(bridge.Graph.NodesOfType<LTXVAddGuideNode>());
        Assert.Equal(2, addGuide.FrameIdx.LiteralAsInt());
        Assert.Equal(0.55, addGuide.Strength.LiteralAsDouble());
        Assert.Same(preprocess.OutputImage, addGuide.Image.Connection);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        LTXVCropGuidesNode crop = Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        Assert.Same(OutputOf(bridge, sampler).VideoLatent, crop.LatentInput.Connection);

        VAEDecodeTiledNode decode = Assert.IsType<VAEDecodeTiledNode>(
            live.FinalVideoSave().Images.Connection?.Node);
        Assert.Same(crop.Latent, decode.Samples.Connection);
        // Defaults come from core's WGNodeData.
        Assert.Equal(2048, decode.TileSize.LiteralAsInt());
        Assert.Equal(256, decode.Overlap.LiteralAsInt());
        Assert.Equal(64, decode.TemporalSize.LiteralAsInt());
        Assert.Equal(16, decode.TemporalOverlap.LiteralAsInt());

        live.AssertAllLive(preprocess, addGuide, crop, decode, sampler);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Every_stage_of_a_chain_gets_its_own_add_guide_for_a_later_ref_frame()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = fixture.Stage(control: 0.5, steps: 10);
        JObject second = fixture.Stage("PreviousStage", control: 0.5, steps: 12);
        first["keyframeStrengths"] = new JArray(0.55);
        second["keyframeStrengths"] = new JArray(0.65);
        JObject clip = MakeClipWithRefs(refs: [MakeRef("Base", frame: 2)], first, second);
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count);

        double[] strengths = [0.55, 0.65];
        for (int stage = 0; stage < strengths.Length; stage++)
        {
            SwarmKSamplerNode sampler = StageSampler(bridge, stage);
            LTXVAddGuideNode addGuide = Assert.Single(
                bridge.Graph.NodesOfType<LTXVAddGuideNode>(),
                node => ReferenceEquals(sampler.Positive.Connection?.Node, node));
            Assert.Equal(2, addGuide.FrameIdx.LiteralAsInt());
            Assert.Equal(strengths[stage], addGuide.Strength.LiteralAsDouble());
            // Each crop must remove its own stage's guide frames.
            LTXVCropGuidesNode crop = Assert.Single(
                bridge.Graph.NodesOfType<LTXVCropGuidesNode>(),
                node => ReferenceEquals(
                    node.LatentInput.Connection?.Node, OutputOf(bridge, sampler)));
            Assert.Same(addGuide.Positive, crop.PositiveInput.Connection);
            live.AssertAllLive(addGuide, crop, sampler);
        }

        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_latent_model_upscale_reads_the_cropped_video_latent_not_the_raw_split()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = fixture.Stage(control: 0.5, steps: 10);
        JObject second = fixture.Stage(
            "PreviousStage", control: 0.5, upscale: 1.5, upscaleMethod: LtxV15SpatialUpscaler,
            steps: 12);
        first["keyframeStrengths"] = new JArray(0.55);
        second["keyframeStrengths"] = new JArray(0.65);
        JObject clip = MakeClipWithRefs(refs: [MakeRef("Base", frame: 2)], first, second);
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstSampler = StageSampler(bridge, 0);
        LTXVCropGuidesNode firstCrop = Assert.Single(
            bridge.Graph.NodesOfType<LTXVCropGuidesNode>(),
            crop => ReferenceEquals(
                crop.LatentInput.Connection?.Node, OutputOf(bridge, firstSampler)));
        LTXVLatentUpsamplerNode upsampler = Assert.Single(
            bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());
        Assert.Same(firstCrop.Latent, upsampler.Samples.Connection);
        SwarmKSamplerNode secondSampler = StageSampler(bridge, 1);
        SwarmResizeLatentPixelMismatchNode select = Assert.IsType<SwarmResizeLatentPixelMismatchNode>(
            Assert.IsType<LTXVAddGuideNode>(
                JointLatentOf(secondSampler).VideoLatent.Connection?.Node)
                .LatentInput.Connection?.Node);
        Assert.Same(upsampler.LATENT, select.Samples.Connection);
        VAEEncodeNode resized = Assert.IsType<VAEEncodeNode>(select.Resized.Connection?.Node);
        ImageScaleNode resize = Assert.IsType<ImageScaleNode>(resized.Pixels.Connection?.Node);
        Assert.Equal(768, resize.Width.LiteralAsInt());
        Assert.Equal(768, resize.Height.LiteralAsInt());
        Assert.Same(upsampler.LATENT,
            Assert.IsType<VAEDecodeTiledNode>(resize.Image.Connection?.Node).Samples.Connection);
        // Remove stage 1's guide frames before publishing.
        LTXVCropGuidesNode secondCrop = Assert.Single(
            bridge.Graph.NodesOfType<LTXVCropGuidesNode>(),
            crop => ReferenceEquals(
                crop.LatentInput.Connection?.Node, OutputOf(bridge, secondSampler)));
        Assert.Same(
            secondCrop.Latent,
            Assert.IsType<VAEDecodeTiledNode>(live.FinalVideoSave().Images.Connection?.Node)
                .Samples.Connection);

        live.AssertAllLive(firstCrop, secondCrop, upsampler, firstSampler, secondSampler);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_pixel_upscale_runs_in_pixel_space_and_re_encodes_at_the_upscaled_size()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(MakeClip(1.0,
                    fixture.Stage(control: 0.5, steps: 8),
                    fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5,
                        upscaleMethod: "pixel-lanczos", steps: 8)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());
        // Guide framing scales all crop to centre; the upscale is the only one that does not.
        ImageScaleNode upscale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Crop.LiteralAsString() == "disabled");
        Assert.Equal(768, upscale.Width.LiteralAsInt());
        Assert.Equal(768, upscale.Height.LiteralAsInt());
        Assert.Equal("lanczos", upscale.UpscaleMethod.LiteralAsString());

        // OutputOf rejects duplicate AV separators per sampler.
        LTXVSeparateAVLatentNode firstSplit = OutputOf(bridge, StageSampler(bridge, 0));
        OutputOf(bridge, StageSampler(bridge, 1));
        Assert.Same(
            firstSplit.VideoLatent,
            Assert.IsType<VAEDecodeTiledNode>(upscale.Image.Connection?.Node).Samples.Connection);

        VAEEncodeNode encode = Assert.Single(bridge.Graph.NodesOfType<VAEEncodeNode>());
        Assert.Same(upscale.IMAGE, encode.Pixels.Connection);
        Assert.Same(
            encode.LATENT,
            Assert.IsType<LTXVImgToVideoInplaceNode>(
                JointLatentOf(StageSampler(bridge, 1)).VideoLatent.Connection?.Node)
                .LatentInput.Connection);
        Assert.Equal(768, generator.CurrentMedia.Width);
        Assert.Equal(768, generator.CurrentMedia.Height);

        live.AssertAllLive(upscale, encode, StageSampler(bridge, 0), StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Chained_latent_model_upscales_frame_each_guide_at_its_own_resolution()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(1.0,
            fixture.Stage(control: 0.5, steps: 8),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5,
                upscaleMethod: LtxV15SpatialUpscaler, steps: 8),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5,
                upscaleMethod: LtxV15SpatialUpscaler, steps: 8)));
        document["width"] = 384;
        document["height"] = 640;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>().Count);
        (int Width, int Height)[] expected = [(384, 640), (576, 960), (864, 1440)];
        for (int stage = 0; stage < expected.Length; stage++)
        {
            LTXVImgToVideoInplaceNode guide = Assert.IsType<LTXVImgToVideoInplaceNode>(
                JointLatentOf(StageSampler(bridge, stage)).VideoLatent.Connection?.Node);
            ImageScaleNode framing = FramingOf(
                Assert.IsType<LTXVPreprocessNode>(guide.Image.Connection?.Node));
            Assert.Equal(expected[stage].Width, framing.Width.LiteralAsInt());
            Assert.Equal(expected[stage].Height, framing.Height.LiteralAsInt());
            // Stage 0 frames the host base image; the upscaled stages each frame their own decode.
            Assert.IsNotType<ImageScaleNode>(framing.Image.Connection?.Node);
            live.AssertLive(framing);
        }

        Assert.Equal(864, generator.CurrentMedia.Width);
        Assert.Equal(1440, generator.CurrentMedia.Height);
        AssertShippable(bridge, workflow, live);
    }

    // Text-to-video prunes core’s apply chain; the captured preprocessor output survives through the timeline guide.
    [Fact]
    public async Task Text_to_video_stage_guide_reuses_cores_controlnet_preprocessor()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(Ltx2WorkflowFixture.Create());

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(IcLoraClip(fixture.Stage(control: 0.5))),
            RequestControlNet);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ResizeImageMaskNodeNode guideResize = GuideResizeOverCorePreprocessor(bridge);
        ImageFromBatchNode guideFrames = GuideFramesOf(bridge);
        Assert.Same(guideResize.Resized, guideFrames.Image.Connection);
        Assert.Equal(0, guideFrames.BatchIndex.LiteralAsInt());
        Assert.Equal(fixture.ExpectedGeneratedFrames, guideFrames.Length.LiteralAsInt());

        // Core's own apply chain conditioned a root the timeline replaced, so it is gone entirely.
        Assert.Empty(bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ControlNetLoaderNode>());

        live.AssertAllLive(guideResize, guideFrames, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Image_to_video_keeps_cores_first_frame_apply_while_the_stage_guide_takes_every_frame()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(
            Ltx2WorkflowFixture.CreateWithBaseModel());

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(IcLoraClip(fixture.Stage(control: 0.5))),
            RequestControlNet);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ResizeImageMaskNodeNode guideResize = GuideResizeOverCorePreprocessor(bridge);
        ControlNetApplyAdvancedNode apply = Assert.Single(
            bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>());
        ImageFromBatchNode firstFrame =
            AssertCoreUnwrapsFirstFrame(workflow, guideResize, apply.Image);

        ImageFromBatchNode guideFrames = GuideFramesOf(bridge);
        Assert.Same(guideResize.Resized, guideFrames.Image.Connection);
        Assert.Equal(fixture.ExpectedGeneratedFrames, guideFrames.Length.LiteralAsInt());
        Assert.NotSame(firstFrame, guideFrames);

        live.AssertAllLive(guideResize, firstFrame, apply, guideFrames, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Clip_length_from_controlnet_wires_the_captured_batch_count_into_every_length()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(
            Ltx2WorkflowFixture.CreateWithBaseModel());
        JObject clip = IcLoraClip(fixture.Stage(control: 0.5));
        clip["clipLengthFromControlNet"] = true;

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(clip), RequestControlNet);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ResizeImageMaskNodeNode guideResize = GuideResizeOverCorePreprocessor(bridge);
        GetImageSizeNode size = Assert.Single(bridge.Graph.NodesOfType<GetImageSizeNode>());
        Assert.Same(guideResize.Resized, size.Image.Connection);

        Assert.Same(
            size.BatchSize,
            Assert.Single(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>()).Length.Connection);
        Assert.Same(
            size.BatchSize,
            Assert.Single(bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>())
                .FramesNumber.Connection);
        Assert.Same(size.BatchSize, GuideFramesOf(bridge).Length.Connection);

        live.AssertAllLive(guideResize, size, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_stage_guide_resize_is_inserted_when_core_ran_no_preprocessor()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(
            Ltx2WorkflowFixture.CreateWithBaseModel());
        JObject clip = IcLoraClip(fixture.Stage(control: 0.5));
        clip["clipLengthFromControlNet"] = true;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip), post =>
        {
            RequestControlNet(post);
            post.Remove("controlnetpreprocessor");
        });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType(PreprocessorClass));
        ResizeImageMaskNodeNode guideResize = Assert.Single(
            bridge.Graph.NodesOfType<ResizeImageMaskNodeNode>());
        Assert.Equal(64, guideResize.ExtraInputs?["resize_type.multiple"]?.Value<int>());
        Assert.IsType<ImageScaleNode>(guideResize.Input.Connection?.Node);

        AssertCoreUnwrapsFirstFrame(
            workflow,
            guideResize,
            Assert.Single(bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>()).Image);

        GetImageSizeNode size = Assert.Single(bridge.Graph.NodesOfType<GetImageSizeNode>());
        Assert.Same(guideResize.Resized, size.Image.Connection);
        ImageFromBatchNode guideFrames = GuideFramesOf(bridge);
        Assert.Same(guideResize.Resized, guideFrames.Image.Connection);
        Assert.Same(size.BatchSize, guideFrames.Length.Connection);

        live.AssertAllLive(guideResize, size, guideFrames, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Controlnet_length_survives_a_latent_model_upscale_on_the_next_stage()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(Ltx2WorkflowFixture.Create());
        JObject clip = IcLoraClip(
            fixture.Stage(control: 0.5),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5,
                upscaleMethod: LtxV15SpatialUpscaler));
        clip["clipLengthFromControlNet"] = true;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip), RequestControlNet);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        LTXVCropGuidesNode firstCrop = Assert.Single(
            bridge.Graph.NodesOfType<LTXVCropGuidesNode>(),
            crop => ReferenceEquals(crop.LatentInput.Connection?.Node, OutputOf(bridge, first)));
        LTXVLatentUpsamplerNode upsampler = Assert.Single(
            bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());
        Assert.Same(firstCrop.Latent, upsampler.Samples.Connection);
        // Stage 1 adds its own guide frames and must crop them before publishing.
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        LTXVCropGuidesNode secondCrop = Assert.Single(
            bridge.Graph.NodesOfType<LTXVCropGuidesNode>(),
            crop => ReferenceEquals(crop.LatentInput.Connection?.Node, OutputOf(bridge, second)));
        Assert.Same(
            secondCrop.Latent,
            Assert.IsType<VAEDecodeTiledNode>(live.FinalVideoSave().Images.Connection?.Node)
                .Samples.Connection);

        // Each guide batch must read the captured count directly, even when refining stage 0.
        GetImageSizeNode size = Assert.Single(bridge.Graph.NodesOfType<GetImageSizeNode>());
        IReadOnlyList<LTXAddVideoICLoRAGuideNode> guides =
            [.. bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>()];
        Assert.Equal(2, guides.Count);
        Assert.All(
            guides,
            guide => Assert.Same(
                size.BatchSize,
                Assert.IsType<ImageFromBatchNode>(guide.Image.Connection?.Node).Length.Connection));

        live.AssertAllLive(firstCrop, secondCrop, upsampler, first, second);
        AssertShippable(bridge, workflow, live);
    }

    // Two seconds distinguish authored length (49 aligned frames) from the request default (25).
    [Fact]
    public async Task Clip_length_from_an_uncaptured_controlnet_warns_and_keeps_the_authored_length()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(Ltx2WorkflowFixture.Create());
        JObject clip = IcLoraClip(fixture.Stage());
        clip["duration"] = 2.0;
        clip["clipLengthFromControlNet"] = true;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<GetImageSizeNode>());
        // The LoRA resolved and still patches the model, so the missing guide is the capture's
        // fault rather than uninstalled weights.
        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Equal(
            49,
            Assert.Single(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>())
                .Length.LiteralAsInt());
        // Once, not once per stage or once per IC-LoRA entry: the clip has one length.
        Assert.Single(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("ControlNet 1 owns clip 0 length")
                && warning.Contains("using the authored clip length instead"));

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Stage_controlnet_strength_scales_each_stages_guide()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(Ltx2WorkflowFixture.Create());
        JObject first = fixture.Stage(control: 0.5);
        JObject second = fixture.Stage("PreviousStage", control: 0.5);
        first["controlNetStrength"] = 0.7;
        second["controlNetStrength"] = 0.3;

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(IcLoraClip(first, second)), RequestControlNet);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        double[] strengths = [0.7, 0.3];
        for (int stage = 0; stage < strengths.Length; stage++)
        {
            SwarmKSamplerNode sampler = StageSampler(bridge, stage);
            LTXAddVideoICLoRAGuideNode guide = Assert.Single(
                bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>(),
                node => ReferenceEquals(sampler.Positive.Connection?.Node, node));
            Assert.Equal(strengths[stage], guide.Strength.LiteralAsDouble());
            live.AssertAllLive(guide, sampler);
        }

        AssertShippable(bridge, workflow, live);
    }

    [Theory]
    [InlineData(0.6, 1)]
    [InlineData(0.0, 0)]
    public async Task Zero_stage_controlnet_strength_drops_the_ic_lora(
        double strength,
        int expectedNodes)
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(Ltx2WorkflowFixture.Create());
        JObject stage = fixture.Stage(control: 0.5);
        stage["controlNetStrength"] = strength;

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(IcLoraClip(stage)), RequestControlNet);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(
            expectedNodes,
            bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>().Count);
        Assert.Equal(expectedNodes, bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count);

        LTXICLoRALoaderModelOnlyNode[] loaders =
            [.. bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>()];
        Assert.Equal(expectedNodes, loaders.Length);
        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        if (loaders.Length > 0)
        {
            Assert.Same(loaders[0], sampler.Model.Connection?.Node);
        }

        live.AssertAllLive([.. loaders, sampler]);
        AssertShippable(bridge, workflow, live);
    }

    // Clear ltxvideo on the generator: the harness adds it to its default feature set.
    [Fact]
    public async Task An_ic_lora_controlnet_guide_refuses_without_the_ltxvideo_nodes()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(Ltx2WorkflowFixture.Create());

        // The exact type is the claim: an internal crash escaping the route is also an
        // InvalidOperationException, and only a readable refusal reaches the user as a message.
        SwarmReadableErrorException refusal = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.Post(
                    MakeDocument(IcLoraClip(fixture.Stage(control: 0.5))),
                    RequestControlNet),
                extraSteps: [new(
                    g => g.Features.Remove(Ltx2HostIntegration.FeatureFlag),
                    double.MinValue)]));

        // The bare repo name would also match the install URL the message quotes.
        Assert.Contains("IC-LoRAs require the ComfyUI-LTXVideo custom nodes", refusal.Message);
    }

    // The captured source’s audio slot must reach the published soundtrack.
    [Fact]
    public async Task A_controlnet_audio_source_publishes_the_captured_source_audio()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(
            Ltx2WorkflowFixture.CreateWithBaseModel());
        JObject clip = MakeClip(1.0, fixture.Stage(control: 0.5));
        clip["audioSource"] = MediaSource.ControlNet;

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(clip), RequestControlNet);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVAudioVAEEncodeNode encode = EncodeOfCapturedControlNetAudio(bridge);
        Assert.True(
            ReachesUpstream(bridge, live.PublishedAudio(), encode.Id),
            "The captured ControlNet audio does not reach the published save's audio.");
        // Positive control: an unresolved capture falls back to a generated silent track.
        Assert.Empty(bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>());

        live.AssertAllLive(encode, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_second_slot_controlnet_audio_source_is_captured_under_its_own_index()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(
            Ltx2WorkflowFixture.CreateWithBaseModel());
        JObject clip = IcLoraClip(MediaSource.ControlNetTwo, fixture.Stage(control: 0.5));
        clip["audioSource"] = MediaSource.ControlNet;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip), post =>
        {
            RequestControlNet(post);
            RequestControlNetTwo(post);
        });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Both slots ran, so a scan for "the one captured index" would find two and give up.
        IReadOnlyList<ControlNetApplyAdvancedNode> applies =
            [.. bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>()];
        Assert.Equal(2, applies.Count);
        LTXVAudioVAEEncodeNode encode = EncodeOfCapturedControlNetAudio(bridge);
        Assert.True(
            ReachesUpstream(bridge, live.PublishedAudio(), encode.Id),
            "The captured ControlNet 2 audio does not reach the published save's audio.");
        Assert.Empty(bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>());

        // The same slot drives the visual guide. Core chains the slots in order, so slot 2's apply
        // is the one conditioning slot 1's output — the only handle on which chain is which here,
        // since both slots load the same model off the same upload.
        ControlNetApplyAdvancedNode slotTwo = Assert.Single(
            applies,
            apply => apply.PositiveInput.Connection?.Node is ControlNetApplyAdvancedNode);
        ResizeImageMaskNodeNode guideResize = Assert.IsType<ResizeImageMaskNodeNode>(
            Assert.IsType<ImageFromBatchNode>(slotTwo.Image.Connection?.Node)
                .Image.Connection?.Node);
        Assert.Same(guideResize.Resized, GuideFramesOf(bridge).Image.Connection);

        live.AssertAllLive(encode, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Image_to_video_captures_a_guide_from_cores_qwen_diffsynth_patch()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(
            Ltx2WorkflowFixture.CreateWithBaseModel());
        ClassifyControlNet(DiffpatchControlNet, "unit/control-diffpatch");

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(IcLoraClip(
                fixture.Stage(control: 0.5),
                fixture.Stage("PreviousStage", control: 0.5))),
            post =>
            {
                RequestControlNet(post);
                post["controlnetmodel"] = DiffpatchControlNet;
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ModelPatchLoaderNode patch = Assert.Single(
            bridge.Graph.NodesOfType<ModelPatchLoaderNode>());
        QwenImageDiffsynthControlnetNode apply = Assert.Single(
            bridge.Graph.NodesOfType<QwenImageDiffsynthControlnetNode>());
        Assert.Same(patch.MODELPATCH, apply.ModelPatch.Connection);
        // Positive control: the model class really did divert core off its default branch.
        Assert.Empty(bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ControlNetLoaderNode>());

        ResizeImageMaskNodeNode guideResize = GuideResizeOverCorePreprocessor(bridge);
        AssertCoreUnwrapsFirstFrame(workflow, guideResize, apply.Image);

        // Each stage guides off the full-length resize, which only holds because the normalizer
        // peels core's single-frame wrap before inserting it.
        IReadOnlyList<LTXAddVideoICLoRAGuideNode> guides =
            [.. bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>()];
        Assert.Equal(2, guides.Count);
        foreach (LTXAddVideoICLoRAGuideNode guide in guides)
        {
            ImageFromBatchNode frames = Assert.IsType<ImageFromBatchNode>(
                guide.Image.Connection?.Node);
            Assert.Same(guideResize.Resized, frames.Image.Connection);
            Assert.Equal(fixture.ExpectedGeneratedFrames, frames.Length.LiteralAsInt());
        }

        live.AssertAllLive([.. guides, patch, apply, guideResize,
            StageSampler(bridge, 0), StageSampler(bridge, 1)]);
        AssertShippable(bridge, workflow, live);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    public async Task Image_to_video_captures_a_guide_from_cores_alimama_inpainting_apply(
        string unionType)
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(
            Ltx2WorkflowFixture.CreateWithBaseModel());
        SetControlNetArchitecture(AliMamaControlNet, "flux.1-dev/controlnet-alimamainpaint");

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(IcLoraClip(fixture.Stage(control: 0.5))),
            post =>
            {
                RequestControlNet(post);
                post["controlnetmodel"] = AliMamaControlNet;
                post["initimage"] = ImagePayload;
                post["maskimage"] = ImagePayload;
                if (unionType is not null)
                {
                    post["controlnetuniontype"] = unionType;
                }
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ControlNetLoaderNode loader = Assert.Single(
            bridge.Graph.NodesOfType<ControlNetLoaderNode>());
        ControlNetInpaintingAliMamaApplyNode apply = Assert.Single(
            bridge.Graph.NodesOfType<ControlNetInpaintingAliMamaApplyNode>());
        // Positive control: the metadata really did divert core off its default branch.
        Assert.Empty(bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>());
        Assert.NotNull(apply.Mask.Connection);
        if (unionType is null)
        {
            Assert.Empty(bridge.Graph.NodesOfType<SetUnionControlNetTypeNode>());
            Assert.Same(loader.CONTROLNET, apply.ControlNet.Connection);
        }
        else
        {
            SetUnionControlNetTypeNode union = Assert.Single(
                bridge.Graph.NodesOfType<SetUnionControlNetTypeNode>());
            Assert.Same(loader.CONTROLNET, union.ControlNet.Connection);
            Assert.Same(union.CONTROLNET, apply.ControlNet.Connection);
        }

        ResizeImageMaskNodeNode guideResize = GuideResizeOverCorePreprocessor(bridge);
        AssertCoreUnwrapsFirstFrame(workflow, guideResize, apply.Image);

        ImageFromBatchNode guideFrames = GuideFramesOf(bridge);
        Assert.Same(guideResize.Resized, guideFrames.Image.Connection);
        Assert.Equal(fixture.ExpectedGeneratedFrames, guideFrames.Length.LiteralAsInt());

        live.AssertAllLive(
            loader, apply, guideResize, guideFrames, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Stages_sharing_an_ic_lora_share_its_loader_and_keep_their_own_guides()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(
            Ltx2WorkflowFixture.CreateWithBaseModel());
        JObject first = fixture.Stage(control: 0.5);
        JObject second = fixture.Stage("PreviousStage", control: 0.5);
        first["controlNetStrength"] = 0.7;
        second["controlNetStrength"] = 0.3;

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(IcLoraClip(first, second)), RequestControlNet);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode shared = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>().Count);

        double[] strengths = [0.7, 0.3];
        HashSet<string> guides = [];
        for (int stage = 0; stage < strengths.Length; stage++)
        {
            SwarmKSamplerNode sampler = StageSampler(bridge, stage);
            Assert.Same(shared, sampler.Model.Connection?.Node);
            LTXAddVideoICLoRAGuideNode guide = Assert.IsType<LTXAddVideoICLoRAGuideNode>(
                sampler.Positive.Connection?.Node);
            Assert.True(
                guides.Add(guide.Id),
                $"Stage {stage} reuses IC-LoRA guide {guide.Id} that an earlier stage claimed.");
            Assert.Equal(strengths[stage], guide.Strength.LiteralAsDouble());
            live.AssertAllLive(shared, guide, sampler);
        }

        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Clip_lora_loads_for_every_stage_alongside_the_ic_lora()
    {
        using Ltx2WorkflowFixture fixture = WithControlNetStubs(
            Ltx2WorkflowFixture.CreateWithBaseModel());
        fixture.InstallModel("LoRA", "UnitTest_ScopedStageLora.safetensors");
        JObject first = fixture.Stage(control: 0.5);
        JObject second = fixture.Stage("PreviousStage", control: 0.5);
        JObject clip = IcLoraClip(first, second);
        foreach (JObject authoredStage in clip["stages"])
        {
            authoredStage["loras"] = new JArray(new JObject
            {
                ["name"] = "UnitTest_ScopedStageLora",
                ["weight"] = 0.6,
            });
        }

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(clip), RequestControlNet);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        IReadOnlyList<LoraLoaderNode> loaders =
            [.. bridge.Graph.NodesOfType<LoraLoaderNode>()];
        LoraLoaderNode loader = Assert.Single(loaders);
        Assert.All(
            loaders,
            loader => Assert.Equal(
                "UnitTest_ScopedStageLora.safetensors", loader.LoraName.LiteralAsString()));

        for (int stage = 0; stage < 2; stage++)
        {
            SwarmKSamplerNode sampler = StageSampler(bridge, stage);
            Assert.True(
                ReachesUpstream(bridge, sampler.Model.Connection?.Node, loader.Id));
            Assert.Equal(0.6, loader.StrengthModel.LiteralAsDouble());
            live.AssertAllLive(loader, sampler);
        }

        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Text_to_video_ignores_host_image_refs_and_captures_no_root_references()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = MakeClipWithRefs(
            refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 2)],
            fixture.Stage("Base", steps: 10));
        clip["duration"] = 1.0;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAddGuideNode>());
        Assert.IsType<EmptyLTXVLatentVideoNode>(JointLatentOf(sampler).VideoLatent.Connection?.Node);

        StageRefStore references = new(generator);
        Assert.Null(references.Generated);
        Assert.Null(references.Base);
        Assert.Null(references.Refiner);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);

        live.AssertAllLive(sampler);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Text_to_video_root_dimensions_keep_one_save_and_scale_nothing()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject document = MakeDocument(MakeClip(1.0, fixture.Stage(steps: 8)));
        document["width"] = 384;
        document["height"] = 512;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(fixture.Post(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ImageScaleNode>());
        EmptyLTXVLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(384, latent.Width.LiteralAsInt());
        Assert.Equal(512, latent.Height.LiteralAsInt());

        AssertPublishesGeneratorMedia(bridge, generator);
        Assert.Equal(384, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);

        live.AssertAllLive(latent, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Text_to_video_stages_hand_the_joint_latent_over_without_pixel_guides()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        JObject workflow = await fixture.GenerateAsync(MakeDocument(MakeClip(1.0,
            fixture.Stage("Base", steps: 8),
            fixture.Stage("Refiner", steps: 8),
            fixture.Stage("Generated", steps: 8))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<ImageScaleNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        SwarmKSamplerNode[] samplers =
            [.. Enumerable.Range(0, 3).Select(id => StageSampler(bridge, id))];
        Assert.Equal(3, bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>().Count);
        Assert.IsType<EmptyLTXVLatentVideoNode>(
            JointLatentOf(samplers[0]).VideoLatent.Connection?.Node);
        for (int stage = 1; stage < samplers.Length; stage++)
        {
            LTXVSeparateAVLatentNode previous = OutputOf(bridge, samplers[stage - 1]);
            LTXVConcatAVLatentNode joint = JointLatentOf(samplers[stage]);
            Assert.Same(previous.VideoLatent, joint.VideoLatent.Connection);
            Assert.Same(previous.AudioLatent, joint.AudioLatent.Connection);
        }

        live.AssertAllLive(samplers);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Clip_shaped_json_without_executable_timeline_does_not_gate_core_generation()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(MakeClip())));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Null(generator.GetVideoExecutionPlanContext());
        // Core's own text-to-video pass, untouched: a planned stage would seed Seed + 42 instead.
        SwarmKSamplerNode core = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(VideoStagesWorkflowFixture.Seed, core.NoiseSeed.LiteralAsLong());
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);

        live.AssertAllLive(core);
        AssertShippable(bridge, workflow, live);
    }
}
