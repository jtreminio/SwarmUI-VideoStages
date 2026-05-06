using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.TypedWorkflowAssertions;

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
        JObject clip = MakeClipConfig(Constants.AudioSourceUpload, stages);
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

            BridgeSync.SyncLastId(g);
        }, 11);

    private static WorkflowGenerator.WorkflowGenStep SeedSavedAudioStageStep(string saveNodeType) =>
        new(g =>
        {
            string audioSource = g.CreateNode("UnitTest_AudioSource", new JObject(), id: "300", idMandatory: false);
            _ = g.CreateNode(saveNodeType, new JObject()
            {
                ["audio"] = new JArray(audioSource, 0)
            }, id: "301", idMandatory: false);

            BridgeSync.SyncLastId(g);
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

        Assert.True(Runner.TryInjectLtxAudio(generator, detection));

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Equal("300", lengthToFrames.AudioInput.Connection!.Node.Id);
        Assert.Equal(0, lengthToFrames.AudioInput.Connection.SlotIndex);

        EmptyLTXVLatentVideoNode emptyVideo = RequireTypedNode<EmptyLTXVLatentVideoNode>(bridge, "108");
        Assert.Same(lengthToFrames.Frames, emptyVideo.Length.Connection);

        LTXVEmptyLatentAudioNode emptyAudio = RequireTypedNode<LTXVEmptyLatentAudioNode>(bridge, "109");
        Assert.Same(lengthToFrames.Frames, emptyAudio.FramesNumber.Connection);
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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Equal("300", lengthToFrames.AudioInput.Connection!.Node.Id);
        Assert.Equal(0, lengthToFrames.AudioInput.Connection.SlotIndex);

        EmptyLTXVLatentVideoNode emptyVideo = RequireTypedNode<EmptyLTXVLatentVideoNode>(bridge, "108");
        Assert.Same(lengthToFrames.Frames, emptyVideo.Length.Connection);

        SetLatentNoiseMaskNode setMask = Assert.Single(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        LTXVConcatAVLatentNode rootConcat = RequireTypedNode<LTXVConcatAVLatentNode>(bridge, "113");
        Assert.Same(setMask.LATENT, rootConcat.AudioLatent.Connection);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>());

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal("203", saveNode.Audio.Connection!.Node.Id);
        Assert.Equal(0, saveNode.Audio.Connection.SlotIndex);

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
                Constants.AudioSourceUpload,
                MakeStage(models.VideoModel.Name))
        ).ToString();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            stagesJson);

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps("SaveAudioMP3"));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal("203", saveNode.Audio.Connection!.Node.Id);
        Assert.Equal(0, saveNode.Audio.Connection.SlotIndex);

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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadAudioB64Node uploadedAudioNode = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());

        LTXVAudioVAEEncodeNode audioEncode = Assert.Single(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Equal(uploadedAudioNode.Id, audioEncode.Audio.Connection!.Node.Id);
        Assert.Equal(0, audioEncode.Audio.Connection.SlotIndex);

        SetLatentNoiseMaskNode setMask = Assert.Single(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        Assert.Same(audioEncode.AudioLatent, setMask.Samples.Connection);

        IReadOnlyList<(ComfyNode Node, INodeInput Input)> maskConsumers = bridge.Graph.FindInputsConnectedTo(setMask.LATENT);
        Assert.Contains(maskConsumers, c => c.Input.Name == "audio_latent" && c.Node is LTXVConcatAVLatentNode);

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        JArray saveAudio = WorkflowBridge.ToPath(saveNode.Audio.Connection!);
        Assert.True(OutputTracesBackToNode(workflow, saveAudio, uploadedAudioNode.Id));
        Assert.False(OutputTracesBackToNode(workflow, saveAudio, "300"));
    }

    [Fact]
    public void Save_audio_stage_matches_video_length_to_uploaded_audio_when_enabled()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject clip = MakeClipConfigWithUpload(
            MakeUploadedAudio(),
            MakeStage(models.VideoModel.Name));
        clip["ClipLengthFromAudio"] = true;
        string stagesJson = MakeRootConfig(clip).ToString();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            stagesJson);

        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps("SaveAudioMP3"));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadAudioB64Node uploadedAudioNode = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());

        SwarmEnsureAudioNode lengthEnsure = Assert.IsType<SwarmEnsureAudioNode>(lengthToFrames.AudioInput.Connection!.Node);
        Assert.Same(uploadedAudioNode.AUDIO, lengthEnsure.Audio.Connection);
        Assert.NotEqual(uploadedAudioNode.Id, lengthToFrames.AudioInput.Connection.Node.Id);
        Assert.NotEqual("300", lengthToFrames.AudioInput.Connection.Node.Id);

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        JArray saveAudio = WorkflowBridge.ToPath(saveNode.Audio.Connection!);
        Assert.True(OutputTracesBackToNode(workflow, saveAudio, uploadedAudioNode.Id));
        Assert.False(OutputTracesBackToNode(workflow, saveAudio, "300"));
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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Equal("300", lengthToFrames.AudioInput.Connection!.Node.Id);
        Assert.Equal(0, lengthToFrames.AudioInput.Connection.SlotIndex);

        LTXVAudioVAEEncodeNode audioEncode = Assert.Single(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Same(lengthToFrames.Audio, audioEncode.Audio.Connection);

        SetLatentNoiseMaskNode setMask = Assert.Single(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        IReadOnlyList<(ComfyNode Node, INodeInput Input)> maskConsumers = bridge.Graph.FindInputsConnectedTo(setMask.LATENT);
        Assert.Contains(maskConsumers, c => c.Input.Name == "audio_latent" && c.Node is LTXVConcatAVLatentNode);

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal("203", saveNode.Audio.Connection!.Node.Id);
        Assert.Equal(0, saveNode.Audio.Connection.SlotIndex);

        LTXVAudioVAEDecodeNode finalAudioDecode = RequireTypedNode<LTXVAudioVAEDecodeNode>(bridge, "203");
        Assert.False(
            finalAudioDecode.Samples.Connection!.Node.Id == "201"
            && finalAudioDecode.Samples.Connection.SlotIndex == 1);

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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<SwarmLoadAudioB64Node> uploadNodes = bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>();
        Assert.Equal(2, uploadNodes.Count);
    }

    [Fact]
    public void Multi_clip_parallel_merge_produces_batch_image_node_routing_to_g_current_media()
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

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps("SaveAudioMP3"));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        BatchImagesNodeNode batchImagesNode = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        AudioConcatNode audioConcatNode = Assert.Single(bridge.Graph.NodesOfType<AudioConcatNode>());

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray(batchImagesNode.Id, 0)));

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);

        JObject batchInputs = (JObject)AsWorkflowNode(batchImagesNode, workflow).Node["inputs"];
        List<JArray> batchImagePaths = batchInputs.Properties()
            .Where(p => p.Value is JArray { Count: 2 })
            .Select(p => (JArray)p.Value)
            .ToList();
        Assert.Equal(2, batchImagePaths.Count);
        foreach (JArray path in batchImagePaths)
        {
            Assert.Contains(samplers, sampler => OutputTracesBackToNode(workflow, path, sampler.Id));
        }
        Assert.NotEqual($"{batchImagePaths[0][0]}", $"{batchImagePaths[1][0]}");

        JArray audio1 = WorkflowBridge.ToPath(audioConcatNode.Audio1.Connection!);
        JArray audio2 = WorkflowBridge.ToPath(audioConcatNode.Audio2.Connection!);
        Assert.Contains(samplers, sampler => OutputTracesBackToNode(workflow, audio1, sampler.Id));
        Assert.Contains(samplers, sampler => OutputTracesBackToNode(workflow, audio2, sampler.Id));

        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        Assert.True(JToken.DeepEquals(
            generator.CurrentMedia.AttachedAudio.Path,
            new JArray(audioConcatNode.Id, 0)));
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
                Constants.AudioSourceNative,
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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadAudioB64Node uploadedAudioNode = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        IReadOnlyList<(ComfyNode Node, INodeInput Input)> uploadConsumers = bridge.Graph.FindInputsConnectedTo(uploadedAudioNode.AUDIO);
        Assert.Contains(uploadConsumers, c => c.Input.Name == "audio" && c.Node is SwarmEnsureAudioNode);
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
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SetLatentNoiseMaskNode setMask = Assert.Single(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        SolidMaskNode solidMask = Assert.IsType<SolidMaskNode>(setMask.Mask.Connection!.Node);

        Assert.Equal(384, solidMask.Width.LiteralAsInt());
        Assert.Equal(640, solidMask.Height.LiteralAsInt());
    }

    private static bool OutputTracesBackToNode(JObject workflow, JArray outputRef, string expectedNodeId)
    {
        Queue<JArray> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(new JArray(outputRef[0], outputRef[1]));
        while (pending.Count > 0)
        {
            JArray current = pending.Dequeue();
            string key = $"{current[0]}::{current[1]}";
            if (!visited.Add(key))
            {
                continue;
            }
            if ($"{current[0]}" == expectedNodeId)
            {
                return true;
            }
            if (!workflow.TryGetValue($"{current[0]}", out JToken nodeToken)
                || nodeToken is not JObject node
                || node["inputs"] is not JObject inputs)
            {
                continue;
            }
            foreach (JProperty input in inputs.Properties())
            {
                if (input.Value is JArray { Count: 2 } inputPath)
                {
                    pending.Enqueue(new JArray(inputPath[0], inputPath[1]));
                }
            }
        }
        return false;
    }
}
