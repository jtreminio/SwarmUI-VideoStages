using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.Ltx2;
using VideoStages.Generated;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class HostVideoRuntimeFlowTests
{
    private static readonly string[] SourceFeatures =
        [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    [Fact]
    public void Hunyuan_15_image_entry_runs_two_real_host_stages()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5");
        T2IModel visionModel = InstallHunyuan15SupportModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(
                    models.VideoModel.Name,
                    "Generated",
                    steps: 8),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0.5,
                    upscale: 2,
                    steps: 10)));
        input.Set(T2IParamTypes.ClipVisionModel, visionModel);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                HostVideoSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(
            2,
            NodesOfClass(bridge, "HunyuanVideo15ImageToVideo").Count());
        Assert.Equal(2, Samplers(bridge).Count());
        Assert.Contains(
            NodesOfClass(bridge, "ImageScale"),
            node => node.FindInput("width").LiteralAsInt() == 1024
                && node.FindInput("height").LiteralAsInt() == 1024);
        Assert.Contains(
            NodesOfClass(bridge, "VAEEncode"),
            encode => bridge.Graph.FindDownstream(encode.FindOutput(0))
                .Any(node => Samplers(bridge).Contains(node)));
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Mochi_text_entry_uses_the_real_host_empty_video_primitive()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatGenmoMochi,
            "genmo-mochi-1");
        InstallMochiSupportModels();
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 9)));

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                HostVideoSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode latent = Assert.Single(
            NodesOfClass(bridge, "EmptyMochiLatentVideo"));
        ComfyNode sampler = Assert.Single(Samplers(bridge));
        Assert.Same(
            latent,
            sampler.FindInput("latent_image").Connection?.Node);
        Assert.Equal(9, sampler.FindInput("steps").LiteralAsInt());
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Ltx2_text_entry_builds_the_host_joint_latent_but_publishes_video_only()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = "lightricks-ltx-video-2",
            Name = "lightricks-ltx-video-2",
            CompatClass = T2IModelClassSorter.CompatLtxv2,
        };
        JObject document = MakeDocument(
            MakeClip(
                MakeStage(models.VideoModel.Name, "Generated", steps: 9)));
        document["fps"] = 24;
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            document.ToString());
        input.Set(T2IParamTypes.VideoFPS, 17);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                HostVideoSteps(),
                [Ltx2HostIntegration.FeatureFlag, "variation_seed"]);
        Assert.Equal(
            24,
            generator.RequireVideoExecutionPlanContext().Plan.FramesPerSecond);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVEmptyLatentAudioNode audioLatent = Assert.Single(
            bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>());
        Assert.Equal(
            24,
            audioLatent.FindInput("frame_rate").LiteralAsDouble());
        LTXVConcatAVLatentNode jointLatent = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>(),
            node => node.AudioLatent.Connection?.Node == audioLatent);
        LTXVConditioningNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConditioningNode>());
        Assert.Equal(
            24,
            conditioning.FindInput("frame_rate").LiteralAsDouble());
        ComfyNode sampler = Assert.Single(Samplers(bridge));
        Assert.Same(jointLatent, sampler.FindInput("latent_image").Connection?.Node);
        Assert.Equal(24, generator.CurrentMedia.FPS);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Equal(17, input.Get(T2IParamTypes.VideoFPS));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Generic_source_entry_uses_the_neutral_video_only_conformance_path()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5");
        T2IModel visionModel = InstallHunyuan15SupportModels();
        JObject clip = MakeClip(
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0.5,
                steps: 8));
        clip["duration"] = 0.6;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,"
                + Convert.ToBase64String([0x11, 0x22, 0x33]),
            ["fileName"] = "host-source.mp4",
            ["startSeconds"] = 1,
        };
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());
        input.Set(T2IParamTypes.ClipVisionModel, visionModel);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                HostVideoSteps(),
                SourceFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node load = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Same(load, components.Video.Connection?.Node);
        SwarmVideoResampleFPSNode resample = Assert.Single(
            bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());
        Assert.Same(components, resample.ImagesInput.Connection?.Node);
        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Same(resample, window.ImagesInput.Connection?.Node);
        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node == window);
        Assert.Equal(512, scale.Width.LiteralAsInt());
        Assert.Equal(512, scale.Height.LiteralAsInt());
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Single(NodesOfClass(bridge, "HunyuanVideo15ImageToVideo"));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Generic_only_timeline_warns_and_ignores_authored_audio_tracks()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5");
        T2IModel visionModel = InstallHunyuan15SupportModels();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8));
        clip["duration"] = 1;
        JObject document = MakeDocument(clip);
        document["audioTracks"] = new JArray(new JObject
        {
            ["id"] = "ignored-overlay",
            ["source"] = new JObject
            {
                ["kind"] = "Upload",
                ["reference"] = "overlay.wav",
                ["uploadedAudio"] = new JObject
                {
                    ["data"] = "data:audio/wav;base64,QUJD",
                    ["fileName"] = "overlay.wav",
                },
            },
            ["spans"] = new JArray(new JObject
            {
                ["timelineStartSeconds"] = 0,
                ["timelineLengthSeconds"] = 0.5,
                ["sourceStartSeconds"] = 0,
            }),
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            document.ToString());
        input.Set(T2IParamTypes.ClipVisionModel, visionModel);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                HostVideoSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Contains(
            generator.RequireVideoExecutionPlanContext().Plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.audio-segments-ignored");
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Single(generator.GetVideoStagesSpec().TimelineAudioSegments);
        Assert.Contains(
            Assert.IsType<List<string>>(input.ExtraMeta["parser_warnings"]),
            warning => warning.Contains(
                "None of the selected video architectures use timeline audio tracks",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Generic_stage_applies_an_ordinary_LoRA_through_the_host_loader()
    {
        using SwarmUiTestContext context = new();
        EnableHostLoraLoading();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5");
        T2IModel visionModel = InstallHunyuan15SupportModels();
        AddLoraModel("UnitTest_HostVideo_Lora.safetensors");
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        stage["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_HostVideo_Lora",
            ["weight"] = 0.45,
            ["textEncoderWeight"] = 0.2,
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(stage));
        input.Set(T2IParamTypes.ClipVisionModel, visionModel);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                HostVideoSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode sampler = Assert.Single(Samplers(bridge));
        ComfyNode lora = Assert.Single(LoraLoaderNodesOf(bridge));
        Assert.Equal("LoraLoaderModelOnly", lora.ClassTypeName);
        Assert.Equal(
            "UnitTest_HostVideo_Lora.safetensors",
            lora.FindInput("lora_name").LiteralAsString());
        Assert.Equal(
            0.45,
            lora.FindInput("strength_model").LiteralAsDouble().Value,
            6);
        Assert.True(ReachesUpstream(
            bridge,
            sampler.FindInput("model").Connection?.Node,
            lora.Id));
        Assert.False(input.TryGet(T2IParamTypes.Loras, out List<string> _));
        Assert.DoesNotContain(
            $"modelloader_{models.VideoModel.Name}_image2video",
            generator.NodeHelpers.Keys);
    }

    [Fact]
    public void Generic_core_pass_ignores_legacy_swap_and_creativity()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5");
        T2IModel visionModel = InstallHunyuan15SupportModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8)));
        input.Set(T2IParamTypes.ClipVisionModel, visionModel);
        input.Set(T2IParamTypes.VideoSwapModel, models.VideoModel);
        input.Set(T2IParamTypes.VideoSwapPercent, 0.3);
        input.Set(T2IParamTypes.Video2VideoCreativity, 0.25);
        int coreSamplerCount = -1;
        int? coreStartStep = null;
        WorkflowGenerator.WorkflowGenStep inspectCore = new(g =>
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            IReadOnlyList<ComfyNode> samplers = Samplers(bridge);
            coreSamplerCount = samplers.Count;
            coreStartStep = Assert.Single(samplers)
                .FindInput("start_at_step")
                .LiteralAsInt();
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        (_, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        inspectCore,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));

        Assert.Equal(1, coreSamplerCount);
        Assert.Equal(0, coreStartStep);
        Assert.Same(
            models.VideoModel,
            input.Get(T2IParamTypes.VideoSwapModel, null));
        Assert.Equal(0.3, input.Get(T2IParamTypes.VideoSwapPercent));
        Assert.Equal(0.25, input.Get(T2IParamTypes.Video2VideoCreativity));
        Assert.Contains(
            generator.RequireVideoExecutionPlanContext().Plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.video-swap-ignored");
    }

    [Fact]
    public void Later_generic_clip_does_not_change_a_specialized_root_request()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModel hostModel = AddHostModel(
            models.VideoModel,
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5",
            "UnitTest_Later_HostVideo.safetensors");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(
                MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 8)),
                MakeClip(MakeStage(hostModel.Name, "Generated", steps: 8)))
                .ToString());
        input.Set(T2IParamTypes.VideoSwapModel, hostModel);
        input.Set(T2IParamTypes.Video2VideoCreativity, 0.25);
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Workflow = [],
        };
        VideoExecutionPlan plan = generator.RequireVideoExecutionPlanContext().Plan;

        HostVideoExecutionAdapter adapter = new(generator);
        Assert.Empty(adapter.PreflightRequest(new(
            plan,
            Ltx2ArchitectureModule.ArchitectureId)));
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code.StartsWith(
                "effective-request.host-video",
                StringComparison.Ordinal));

        WorkflowGenerator.ImageToVideoGenInfo core = new()
        {
            Generator = generator,
            ContextID = T2IParamInput.SectionID_Video,
            VideoSwapModel = hostModel,
            VideoSwapPercent = 0.2,
            VideoEndFrame = new Image([0x01], MediaType.ImagePng),
            StartStep = 4,
        };
        HostVideoCorePassIsolation.Isolate(core);

        Assert.Same(hostModel, core.VideoSwapModel);
        Assert.Equal(0.2, core.VideoSwapPercent);
        Assert.NotNull(core.VideoEndFrame);
        Assert.Equal(4, core.StartStep);
        Assert.False(core.HasMatchedModelData);
    }

    [Fact]
    public void Core_isolation_failure_is_sticky_after_partial_mutation()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8)));
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/",
            Workflow = [],
        };
        VideoExecutionPlanContext request =
            generator.RequireVideoExecutionPlanContext();
        request.PrepareRequest();
        WorkflowGenerator.ImageToVideoGenInfo core = new()
        {
            Generator = generator,
            ContextID = T2IParamInput.SectionID_Video,
            VideoSwapModel = models.VideoModel,
            VideoSwapPercent = 0.2,
            VideoEndFrame = new Image([0x01], MediaType.ImagePng),
            StartStep = 4,
        };

        InvalidOperationException first = Assert.Throws<InvalidOperationException>(
            () => HostVideoCorePassIsolation.Isolate(core));
        core.VideoSwapModel = models.VideoModel;
        InvalidOperationException repeated = Assert.Throws<InvalidOperationException>(
            () => HostVideoCorePassIsolation.Isolate(core));

        Assert.Contains("no live base model", first.Message);
        Assert.Same(first, repeated);
        Assert.Equal(VideoExecutionState.Failed, request.State);
        Assert.Same(models.VideoModel, core.VideoSwapModel);
    }

    [Theory]
    [InlineData("nvidia-cosmos-predict2-t2i-2b", "nvidia-cosmos-predict2-t2i-2b")]
    [InlineData("nvidia-cosmos-predict2-t2i-14b", "nvidia-cosmos-predict2-t2i-14b")]
    public void Cosmos_Predict2_text_to_image_flags_are_rejected_before_graph_mutation(
        string modelClassId,
        string compatibilityClassId)
    {
        using SwarmUiTestContext context = new();
        T2IModelCompatClass compatibility = new()
        {
            ID = compatibilityClassId,
            IsText2Video = true,
            IsImage2Video = true,
        };
        TestModelBundle models = HostModel(compatibility, modelClassId);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8)));

        AssertRejectedBeforeMutation(
            input,
            "does not resolve to a registered video architecture");
    }

    [Fact]
    public void One_clip_cannot_mix_generic_compatibility_classes()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5");
        T2IModel mochi = AddHostModel(
            models.VideoModel,
            T2IModelClassSorter.CompatGenmoMochi,
            "genmo-mochi-1",
            "UnitTest_Mochi_Second.safetensors");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8),
                MakeStage(
                    mochi.Name,
                    "PreviousStage",
                    control: 0.5,
                    steps: 8)));

        AssertRejectedBeforeMutation(
            input,
            "All authored stages in one clip must use one host compatibility class");
    }

    [Fact]
    public void Unsupported_generic_extras_warn_and_do_not_reach_the_host_stage()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5");
        T2IModel visionModel = InstallHunyuan15SupportModels();
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            steps: 8);
        JObject stage = MakeStage(
            models.VideoModel.Name,
            "Base",
            control: 0.5,
            upscale: 2,
            upscaleMethod: "model-not-supported",
            steps: 8);
        stage["controlNetStrength"] = 0.8;
        stage["icLoraStrengths"] = new JArray(1.0);
        stage["refStrengths"] = new JArray(0.7);
        JObject clip = MakeClip(first, stage);
        clip["saveAudioTrack"] = true;
        clip["reuseAudio"] = true;
        clip["clipLengthFromAudio"] = true;
        clip["refFraming"] = "fit";
        clip["icLoras"] = new JArray(new JObject
        {
            ["name"] = "ignored.safetensors",
            ["source"] = "Incoming",
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());
        input.Set(T2IParamTypes.ClipVisionModel, visionModel);
        input.Set(T2IParamTypes.Video2VideoCreativity, 0.2);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                HostVideoSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(2, NodesOfClass(bridge, "HunyuanVideo15ImageToVideo").Count());
        Assert.Contains(
            generator.RequireVideoExecutionPlanContext().Plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-ic-lora-ignored");
        Assert.Contains(
            generator.RequireVideoExecutionPlanContext().Plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-stage-reference-ignored");
        Assert.Contains(
            generator.RequireVideoExecutionPlanContext().Plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-audio-output-ignored");
        Assert.Contains(
            generator.RequireVideoExecutionPlanContext().Plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-audio-reuse-ignored");
        Assert.Contains(
            generator.RequireVideoExecutionPlanContext().Plan.Diagnostics,
            diagnostic => diagnostic.Code
                == "effective-request.unsupported-reference-framing-ignored");
        List<string> warnings = Assert.IsType<List<string>>(
            input.ExtraMeta["parser_warnings"]);
        Assert.Contains(
            warnings,
            warning => warning.Contains(
                "'Video2Video Creativity'",
                StringComparison.Ordinal));
        // An upscale model is a stock ComfyUI operation, so the generic runtime drives it too.
        Assert.NotEmpty(NodesOfClass(bridge, "UpscaleModelLoader"));
        Assert.NotEmpty(NodesOfClass(bridge, "ImageUpscaleWithModel"));
        Assert.DoesNotContain(
            warnings,
            warning => warning.Contains("upscale", StringComparison.OrdinalIgnoreCase));
        VideoStagesSpec authored = generator.GetVideoStagesSpec();
        ClipSpec authoredClip = Assert.Single(authored.Clips);
        Assert.True(authoredClip.SaveAudioTrack);
        Assert.True(authoredClip.ReuseAudio);
        Assert.True(authoredClip.ClipLengthFromAudio);
        Assert.Equal(ReferenceFramingMode.Fit, authoredClip.ReferenceFraming);
        Assert.Equal("Generated", authoredClip.Stages[0].ImageReference);
        Assert.Equal("Base", authoredClip.Stages[1].ImageReference);
        Assert.Equal(0.2, input.Get(T2IParamTypes.Video2VideoCreativity));
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> HostVideoSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static TestModelBundle HostModel(
        T2IModelCompatClass compatibility,
        string modelClassId)
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = modelClassId,
            Name = modelClassId,
            CompatClass = compatibility,
            StandardWidth = 960,
            StandardHeight = 960,
        };
        return models;
    }

    private static T2IModel AddHostModel(
        T2IModel existing,
        T2IModelCompatClass compatibility,
        string modelClassId,
        string name)
    {
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel model = new(handler, TestStubModel.Folder(handler), TestStubModel.File(handler, name), name)
        {
            ModelClass = existing.ModelClass with
            {
                ID = modelClassId,
                Name = modelClassId,
                CompatClass = compatibility,
            },
        };
        handler.Models[model.Name] = model;
        return model;
    }

    private static T2IModel AddLoraModel(string name)
    {
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler handler))
        {
            handler = new() { ModelType = "LoRA" };
            Program.T2IModelSets["LoRA"] = handler;
        }
        T2IModel model = TestStubModel.Create(handler, name);
        handler.Models[model.Name] = model;
        return model;
    }

    private static void EnableHostLoraLoading()
    {
        WorkflowGenerator.AddModelGenStep(g =>
        {
            if (g.LoadingModelType == "negative"
                && !g.UserInput.Get(T2IParamTypes.NegativeModelIncludeLoras, true))
            {
                return;
            }
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(
                -1,
                g.LoadingModel,
                g.LoadingClip);
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(
                0,
                g.LoadingModel,
                g.LoadingClip);
            int confinement = g.IsImageToVideo
                ? T2IParamInput.SectionID_Video
                : T2IParamInput.SectionID_BaseOnly;
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(
                confinement,
                g.LoadingModel,
                g.LoadingClip);
        }, -10);
    }

    private static void AssertRejectedBeforeMutation(
        T2IParamInput input,
        string expectedReason)
    {
        WorkflowGenerator captured = null;
        JObject before = null;
        WorkflowGenerator.WorkflowGenStep snapshot = new(g =>
        {
            captured = g;
            before = (JObject)g.Workflow.DeepClone();
        }, Constants.WorkflowStepPriority.PreflightRequest - 0.1);
        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([snapshot, WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));

        Assert.Contains(expectedReason, error.Message);
        Assert.True(JToken.DeepEquals(before, captured.Workflow));
    }

    private static void InstallMochiSupportModels()
    {
        EnsureSupportFolders();
        Install("Clip", "t5xxl_enconly.safetensors");
        Install("VAE", KnownModel("mochi-vae"));
    }

    private static T2IModel InstallHunyuan15SupportModels()
    {
        EnsureSupportFolders();
        Install("Clip", "qwen_2.5_vl_7b_fp8_scaled.safetensors");
        Install("Clip", "byt5_small_glyphxl_fp16.safetensors");
        Install("VAE", KnownModel("hunyuan-video-1_5-vae"));
        return Install("ClipVision", "sigclip_vision_patch14_384.safetensors");
    }

    private static void EnsureSupportFolders()
    {
        Program.T2IModelSets.TryAdd(
            "VAE",
            new T2IModelHandler { ModelType = "VAE" });
        Program.T2IModelSets.TryAdd(
            "ClipVision",
            new T2IModelHandler { ModelType = "ClipVision" });
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
    }

    private static string KnownModel(string id) =>
        CommonModels.Known[id].FileName;

    private static T2IModel Install(string modelType, string name)
    {
        T2IModelHandler handler = Program.T2IModelSets[modelType];
        T2IModel model = TestStubModel.Create(handler, name);
        handler.Models[name] = model;
        return model;
    }

    private static IEnumerable<ComfyNode> NodesOfClass(
        WorkflowBridge bridge,
        string classType) =>
        bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName == classType);

    private static IReadOnlyList<ComfyNode> Samplers(WorkflowBridge bridge) =>
        NodesOfClass(bridge, "SwarmKSampler")
            .Concat(NodesOfClass(bridge, "KSamplerAdvanced"))
            .ToArray();
}
