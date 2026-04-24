using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.LTX2;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AudioInjectionTests
{
    private const string EmptyLtxvLatentVideo = "EmptyLTXVLatentVideo";
    private const string LtxvEmptyLatentAudio = "LTXVEmptyLatentAudio";
    private const string LtxvImgToVideoInplace = "LTXVImgToVideoInplace";
    private const string LtxvConcatAvLatent = "LTXVConcatAVLatent";
    private const string LtxvSeparateAvLatent = "LTXVSeparateAVLatent";
    private const string LtxvAudioVaeDecode = "LTXVAudioVAEDecode";
    private const string SwarmSaveAnimationWs = "SwarmSaveAnimationWS";
    private const string SwarmAudioLengthToFrames = "SwarmAudioLengthToFrames";
    private const string SwarmLoadAudioB64 = "SwarmLoadAudioB64";
    private const string SwarmEnsureAudio = "SwarmEnsureAudio";
    private const string LtxvAudioVaeEncode = "LTXVAudioVAEEncode";
    private const string SetLatentNoiseMask = "SetLatentNoiseMask";

    private static JObject MakeStage(string model) => new()
    {
        ["Control"] = 1.0,
        ["Upscale"] = 1.0,
        ["UpscaleMethod"] = "pixel-lanczos",
        ["Model"] = model,
        ["Vae"] = "",
        ["Steps"] = 10,
        ["CfgScale"] = 4.5,
        ["Sampler"] = "euler",
        ["Scheduler"] = "normal",
        ["ImageReference"] = "Generated"
    };

    private static JObject MakeClip(int width, int height, params JObject[] stages) => new()
    {
        ["Name"] = "Clip 0",
        ["Width"] = width,
        ["Height"] = height,
        ["Stages"] = new JArray(stages)
    };

    private static JObject MakeClipConfig(string audioSource, params JObject[] stages) => new()
    {
        ["Name"] = "Clip 0",
        ["Width"] = 512,
        ["Height"] = 512,
        ["AudioSource"] = audioSource,
        ["Stages"] = new JArray(stages)
    };

    private static JObject MakeClipConfigWithUpload(JObject uploadedAudio, params JObject[] stages)
    {
        JObject clip = MakeClipConfig(VideoStagesExtension.AudioSourceUpload, stages);
        clip["UploadedAudio"] = uploadedAudio;
        return clip;
    }

    private static JObject MakeRootConfig(JObject clip) => new()
    {
        ["Width"] = 512,
        ["Height"] = 512,
        ["Clips"] = new JArray(clip),
    };

    private static JObject MakeMultiClipRootConfig(params JObject[] clips) => new()
    {
        ["Width"] = 512,
        ["Height"] = 512,
        ["Clips"] = new JArray(clips)
    };

    private static JObject MakeUploadedAudio(
        string data = "data:audio/wav;base64,QUJD",
        string fileName = "clip.wav") => new()
    {
        ["Data"] = data,
        ["FileName"] = fileName
    };

    private static JObject Node(string classType, JObject inputs = null) => new()
    {
        ["class_type"] = classType,
        ["inputs"] = inputs ?? new JObject()
    };

    private static T2IParamInput BuildNativeInput(
        T2IModel baseModel,
        T2IModel videoModel,
        string stagesJson)
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, "unit test prompt");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(VideoStagesExtension.VideoStagesJson, stagesJson);
        input.Set(T2IParamTypes.VideoModel, videoModel);
        input.Set(T2IParamTypes.VideoFrames, 16);
        input.Set(T2IParamTypes.VideoFPS, 24);
        if (Program.T2IModelSets.TryGetValue("Clip", out T2IModelHandler clipHandler)
            && clipHandler.Models.TryGetValue("gemma_3_12B_it.safetensors", out T2IModel gemmaModel))
        {
            input.Set(T2IParamTypes.GemmaModel, gemmaModel);
        }
        return input;
    }

    private static WorkflowGenerator CreateInjectorGenerator(JObject workflow)
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.VideoFPS, 24);

        T2IModel model = new(null!, "", "", "unit-ltxv2")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit-video-ltxv2",
                Name = "Unit Video LTXV2",
                CompatClass = T2IModelClassSorter.CompatLtxv2,
                StandardWidth = 512,
                StandardHeight = 512
            }
        };

        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/",
            Workflow = workflow,
            FinalLoadedModel = model,
            FinalLoadedModelList = [model]
        };
        generator.CurrentAudioVae = new WGNodeData(new JArray("105", 0), generator, WGNodeData.DT_AUDIOVAE, T2IModelClassSorter.CompatLtxv2);
        return generator;
    }

    private static WorkflowGenerator.WorkflowGenStep SeedRootLtxVideoChainStep() =>
        new(g =>
        {
            T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null);
            g.FinalLoadedModel = videoModel;
            g.FinalLoadedModelList = videoModel is null ? [] : [videoModel];

            string videoModelNode = g.CreateNode("UnitTest_VideoModel", new JObject(), id: "103", idMandatory: false);
            g.CurrentModel = new WGNodeData([videoModelNode, 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
            g.CurrentTextEnc = new WGNodeData([videoModelNode, 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());

            string videoVaeNode = g.CreateNode("UnitTest_VideoVae", new JObject(), id: "104", idMandatory: false);
            g.CurrentVae = new WGNodeData([videoVaeNode, 0], g, WGNodeData.DT_VAE, g.CurrentCompat());

            string audioVaeNode = g.CreateNode("UnitTest_AudioVae", new JObject(), id: "105", idMandatory: false);
            g.CurrentAudioVae = new WGNodeData([audioVaeNode, 0], g, WGNodeData.DT_AUDIOVAE, g.CurrentCompat());

            string emptyVideoLatent = g.CreateNode(EmptyLtxvLatentVideo, new JObject()
            {
                ["width"] = 512,
                ["height"] = 512,
                ["length"] = 16,
                ["batch_size"] = 1
            }, id: "108", idMandatory: false);
            string emptyAudioLatent = g.CreateNode(LtxvEmptyLatentAudio, new JObject()
            {
                ["audio_vae"] = new JArray("105", 0),
                ["frames_number"] = 16,
                ["frame_rate"] = 24,
                ["batch_size"] = 1
            }, id: "109", idMandatory: false);
            string imgToVideo = g.CreateNode(LtxvImgToVideoInplace, new JObject()
            {
                ["vae"] = new JArray("104", 0),
                ["image"] = g.CurrentMedia.Path,
                ["latent"] = new JArray(emptyVideoLatent, 0),
                ["strength"] = 1.0,
                ["bypass"] = false
            }, id: "111", idMandatory: false);
            string concat = g.CreateNode(LtxvConcatAvLatent, new JObject()
            {
                ["video_latent"] = new JArray(imgToVideo, 0),
                ["audio_latent"] = new JArray(emptyAudioLatent, 0)
            }, id: "113", idMandatory: false);
            string separate = g.CreateNode(LtxvSeparateAvLatent, new JObject()
            {
                ["av_latent"] = new JArray(concat, 0)
            }, id: "201", idMandatory: false);
            string videoDecode = g.CreateNode("VAEDecodeTiled", new JObject()
            {
                ["vae"] = new JArray("104", 0),
                ["samples"] = new JArray(separate, 0),
                ["tile_size"] = 2048,
                ["overlap"] = 256,
                ["temporal_size"] = 64,
                ["temporal_overlap"] = 16
            }, id: "202", idMandatory: false);
            _ = g.CreateNode(LtxvAudioVaeDecode, new JObject()
            {
                ["audio_vae"] = new JArray("105", 0),
                ["samples"] = new JArray(separate, 1)
            }, id: "203", idMandatory: false);
            _ = g.CreateNode(SwarmSaveAnimationWs, new JObject()
            {
                ["images"] = new JArray(videoDecode, 0),
                ["audio"] = new JArray("203", 0),
                ["fps"] = 24,
                ["lossless"] = false,
                ["quality"] = 95,
                ["method"] = "default",
                ["format"] = "h264-mp4"
            }, id: "9", idMandatory: false);

            g.CurrentMedia = new WGNodeData([videoDecode, 0], g, WGNodeData.DT_VIDEO, g.CurrentCompat())
            {
                Width = 512,
                Height = 512,
                Frames = 16,
                FPS = 24
            };
        }, 11);

    private static WorkflowGenerator.WorkflowGenStep SeedSavedAudioStageStep(string saveNodeType) =>
        new(g =>
        {
            string audioSource = g.CreateNode("UnitTest_AudioSource", new JObject(), id: "300", idMandatory: false);
            _ = g.CreateNode(saveNodeType, new JObject()
            {
                ["audio"] = new JArray(audioSource, 0)
            }, id: "301", idMandatory: false);
        }, 10.2);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildSteps(string saveNodeType) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRootLtxVideoChainStep(), SeedSavedAudioStageStep(saveNodeType)])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    [Fact]
    public void Injector_sets_empty_video_length_from_audio_length_frames_before_cleanup_sensitive_stages_run()
    {
        JObject workflow = new()
        {
            ["105"] = Node("UnitTest_AudioVae"),
            ["108"] = Node(EmptyLtxvLatentVideo, new JObject()
            {
                ["length"] = 16,
                ["width"] = 512,
                ["height"] = 512,
                ["batch_size"] = 1
            }),
            ["109"] = Node(LtxvEmptyLatentAudio, new JObject()
            {
                ["audio_vae"] = new JArray("105", 0),
                ["frames_number"] = 16,
                ["frame_rate"] = 24,
                ["batch_size"] = 1
            }),
            ["113"] = Node(LtxvConcatAvLatent, new JObject()
            {
                ["video_latent"] = new JArray("108", 0),
                ["audio_latent"] = new JArray("109", 0)
            }),
            ["300"] = Node("UnitTest_AudioSource"),
            ["301"] = Node("SaveAudioMP3", new JObject()
            {
                ["audio"] = new JArray("300", 0)
            }),
            ["400"] = Node("UnitTest_LatentAudit", new JObject()
            {
                ["latent"] = new JArray("109", 0)
            })
        };

        WorkflowGenerator generator = CreateInjectorGenerator(workflow);
        AudioStageDetector.Detection detection = new AudioStageDetector(generator).Detect();

        Assert.True(new LtxAudioInjector(generator).TryInject(detection));

        WorkflowNode lengthToFrames = WorkflowAssertions.RequireNodeOfType(workflow, SwarmAudioLengthToFrames);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(lengthToFrames.Node, "audio"),
            new JArray("300", 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(WorkflowAssertions.RequireNodeById(workflow, "108").Node, "length"),
            new JArray(lengthToFrames.Id, 1)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(WorkflowAssertions.RequireNodeById(workflow, "109").Node, "frames_number"),
            new JArray(lengthToFrames.Id, 1)));
    }

    [Fact]
    public void Save_audio_stage_injects_audio_into_native_ltx_video_chain_without_configured_stages()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[]");

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps("SaveAudioMP3"));

        WorkflowNode lengthToFrames = WorkflowAssertions.RequireNodeOfType(workflow, SwarmAudioLengthToFrames);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(lengthToFrames.Node, "audio"),
            new JArray("300", 0)));
        WorkflowNode emptyVideo = WorkflowAssertions.RequireNodeById(workflow, "108");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(emptyVideo.Node, "length"),
            new JArray(lengthToFrames.Id, 1)));

        WorkflowNode setMask = WorkflowAssertions.RequireNodeOfType(workflow, SetLatentNoiseMask);
        WorkflowNode rootConcat = WorkflowAssertions.RequireNodeById(workflow, "113");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(rootConcat.Node, "audio_latent"),
            new JArray(setMask.Id, 0)));

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, LtxvEmptyLatentAudio));

        WorkflowNode saveNode = WorkflowAssertions.RequireNodeOfType(workflow, SwarmSaveAnimationWs);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(saveNode.Node, "audio"),
            new JArray("203", 0)));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
    }

    [Fact]
    public void Save_audio_stage_does_not_inject_uploaded_audio_when_upload_is_requested_without_payload()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = MakeRootConfig(
            MakeClipConfig(
                VideoStagesExtension.AudioSourceUpload,
                MakeStage(models.VideoModel.Name))
        ).ToString();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            stagesJson);

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps("SaveAudioMP3"));

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, SwarmAudioLengthToFrames));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, SwarmLoadAudioB64));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, SetLatentNoiseMask));

        WorkflowNode saveNode = WorkflowAssertions.RequireNodeOfType(workflow, SwarmSaveAnimationWs);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(saveNode.Node, "audio"),
            new JArray("203", 0)));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
    }

    [Fact]
    public void Save_audio_stage_injects_uploaded_audio_when_upload_is_requested()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = MakeRootConfig(
            MakeClipConfigWithUpload(
                MakeUploadedAudio(),
                MakeStage(models.VideoModel.Name))
        ).ToString();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            stagesJson);

        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps("SaveAudioMP3"));

        WorkflowNode uploadedAudioNode = WorkflowAssertions.RequireNodeOfType(workflow, SwarmLoadAudioB64);
        WorkflowNode lengthToFrames = WorkflowAssertions.RequireNodeOfType(workflow, SwarmAudioLengthToFrames);
        JArray lengthAudioIn = WorkflowAssertions.RequireConnectionInput(lengthToFrames.Node, "audio");
        WorkflowNode lengthEnsure = WorkflowAssertions.RequireNodeById(workflow, $"{lengthAudioIn[0]}");
        Assert.Equal(SwarmEnsureAudio, $"{lengthEnsure.Node["class_type"]}");
        JArray ensureAudioIn = WorkflowAssertions.RequireConnectionInput(lengthEnsure.Node, "audio");
        Assert.Equal(uploadedAudioNode.Id, $"{ensureAudioIn[0]}");
        Assert.Equal(0, ensureAudioIn[1].Value<int>());
        Assert.NotEqual(uploadedAudioNode.Id, $"{lengthAudioIn[0]}");
        Assert.NotEqual("300", $"{lengthAudioIn[0]}");

        WorkflowNode audioEncode = Assert.Single(WorkflowUtils.NodesOfType(workflow, LtxvAudioVaeEncode));
        WorkflowNode setMask = Assert.Single(WorkflowUtils.NodesOfType(workflow, SetLatentNoiseMask));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(setMask.Node, "samples"),
            new JArray(audioEncode.Id, 0)));
        IReadOnlyList<WorkflowInputConnection> maskConsumers = WorkflowUtils.FindInputConnections(workflow, new JArray(setMask.Id, 0));
        Assert.Contains(maskConsumers, connection =>
            connection.InputName == "audio_latent"
            && $"{WorkflowAssertions.RequireNodeById(workflow, connection.NodeId).Node["class_type"]}" == LtxvConcatAvLatent);
    }

    [Fact]
    public void Save_audio_stage_injects_audio_into_native_ltx_video_chain_before_stages_run()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(MakeClip(512, 512, MakeStage(models.VideoModel.Name))).ToString();
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps("SaveAudioMP3"));

        WorkflowNode lengthToFrames = WorkflowAssertions.RequireNodeOfType(workflow, SwarmAudioLengthToFrames);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(lengthToFrames.Node, "audio"),
            new JArray("300", 0)));

        WorkflowNode audioEncode = WorkflowAssertions.RequireNodeOfType(workflow, LtxvAudioVaeEncode);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(audioEncode.Node, "audio"),
            new JArray(lengthToFrames.Id, 0)));

        WorkflowNode setMask = WorkflowAssertions.RequireNodeOfType(workflow, SetLatentNoiseMask);
        IReadOnlyList<WorkflowInputConnection> maskConsumers = WorkflowUtils.FindInputConnections(workflow, new JArray(setMask.Id, 0));
        Assert.Contains(maskConsumers, connection =>
            connection.InputName == "audio_latent"
            && $"{WorkflowAssertions.RequireNodeById(workflow, connection.NodeId).Node["class_type"]}" == LtxvConcatAvLatent);

        WorkflowNode saveNode = WorkflowAssertions.RequireNodeOfType(workflow, SwarmSaveAnimationWs);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(saveNode.Node, "audio"),
            new JArray("203", 0)));

        WorkflowNode finalAudioDecode = WorkflowAssertions.RequireNodeById(workflow, "203");
        Assert.False(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(finalAudioDecode.Node, "samples"),
            new JArray("201", 1)));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
    }

    [Fact]
    public void Save_audio_stage_creates_one_load_audio_node_per_upload_mode_clip()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = MakeMultiClipRootConfig(
            MakeClipConfigWithUpload(
                MakeUploadedAudio(data: "data:audio/wav;base64,QUFB", fileName: "first.wav"),
                MakeStage(models.VideoModel.Name)),
            MakeClipConfigWithUpload(
                MakeUploadedAudio(data: "data:audio/wav;base64,QkJC", fileName: "second.wav"),
                MakeStage(models.VideoModel.Name))
        ).ToString();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            stagesJson);

        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps("SaveAudioMP3"));

        IReadOnlyList<WorkflowNode> uploadNodes = WorkflowUtils.NodesOfType(workflow, SwarmLoadAudioB64);
        Assert.Equal(2, uploadNodes.Count);
    }

    [Fact]
    public void Save_audio_stage_uses_clip_uploaded_audio_when_switching_from_native_to_upload_clip()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = MakeMultiClipRootConfig(
            MakeClipConfig(
                VideoStagesExtension.AudioSourceNative,
                MakeStage(models.VideoModel.Name)),
            MakeClipConfigWithUpload(
                MakeUploadedAudio(data: "data:audio/wav;base64,QkJC", fileName: "second.wav"),
                MakeStage(models.VideoModel.Name))
        ).ToString();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            stagesJson);

        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps("SaveAudioMP3"));

        WorkflowNode uploadedAudioNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, SwarmLoadAudioB64));
        IReadOnlyList<WorkflowInputConnection> uploadConsumers = WorkflowUtils.FindInputConnections(
            workflow,
            new JArray(uploadedAudioNode.Id, 0));
        Assert.Contains(uploadConsumers, connection =>
            connection.InputName == "audio"
            && $"{WorkflowAssertions.RequireNodeById(workflow, connection.NodeId).Node["class_type"]}" == SwarmEnsureAudio);
    }

    [Fact]
    public void Save_audio_stage_uses_root_stage_resolution_for_injected_audio_mask()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeClip(width: 384, height: 640, MakeStage(models.VideoModel.Name))
        ).ToString();
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);

        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps("SaveAudioMP3"));

        WorkflowNode setMask = WorkflowAssertions.RequireNodeOfType(workflow, SetLatentNoiseMask);
        WorkflowNode solidMask = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(setMask.Node, "mask")[0]}");

        Assert.Equal(384, solidMask.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(640, solidMask.Node["inputs"]?.Value<int>("height"));
    }
}
