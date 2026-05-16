using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AudioInjectionTests
{
    // Local override of Fixtures.MakeStage: pins Steps=10 and ImageReference="Generated" for audio-injection tests.
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

    private static JObject MakeClipConfig(string audioSource, params JObject[] stages) => new()
    {
        ["Name"] = "Clip 0",
        ["AudioSource"] = audioSource,
        ["Stages"] = new JArray(stages)
    };

    private static JObject MakeClipConfigWithUpload(JObject uploadedAudio, params JObject[] stages)
    {
        JObject clip = MakeClipConfig(Constants.AudioSourceUpload, stages);
        clip["UploadedAudio"] = uploadedAudio;
        return clip;
    }

    private static JObject MakeUploadedAudio(
        string data = "data:audio/wav;base64,QUJD",
        string fileName = "clip.wav") => new()
    {
        ["Data"] = data,
        ["FileName"] = fileName
    };

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
        generator.CurrentAudioVae = new WGNodeData(new JArray("105", 0), generator,
            WGNodeData.DT_AUDIOVAE, T2IModelClassSorter.CompatLtxv2);
        return generator;
    }

    private static WorkflowGenerator.WorkflowGenStep SeedRootLtxVideoChainStep() =>
        new(g =>
        {
            T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null);
            g.FinalLoadedModel = videoModel;
            g.FinalLoadedModelList = videoModel is null ? [] : [videoModel];

            using SyncingWorkflowBridge bridge = BridgeSync.For(g);

            UnknownNode videoModelNode = bridge.AddStub("UnitTest_VideoModel", "103").WithOutputs(WGNodeData.DT_MODEL, "CLIP");
            g.CurrentModel = videoModelNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);
            g.CurrentTextEnc = videoModelNode.GetOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC);

            UnknownNode videoVaeNode = bridge.AddStub("UnitTest_VideoVae", "104").WithOutputs(WGNodeData.DT_VAE);
            g.CurrentVae = videoVaeNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_VAE);

            UnknownNode audioVaeNode = bridge.AddStub("UnitTest_AudioVae", "105").WithOutputs(WGNodeData.DT_VAE);
            g.CurrentAudioVae = audioVaeNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_AUDIOVAE);

            EmptyLTXVLatentVideoNode emptyVideoLatent = new EmptyLTXVLatentVideoNode()
                .With(Width: 512, Height: 512, Length: 16, BatchSize: 1);
            bridge.AddNode(emptyVideoLatent, "108");

            LTXVEmptyLatentAudioNode emptyAudioLatent = new LTXVEmptyLatentAudioNode()
                .With(FramesNumber: 16, FrameRate: 24, BatchSize: 1);
            emptyAudioLatent.AudioVae.ConnectToUntyped(audioVaeNode.GetOutput(0));
            bridge.AddNode(emptyAudioLatent, "109");

            LTXVImgToVideoInplaceNode imgToVideo = new LTXVImgToVideoInplaceNode().With(Strength: 1.0, Bypass: false);
            imgToVideo.Vae.ConnectToUntyped(videoVaeNode.GetOutput(0));
            imgToVideo.Image.ConnectToUntyped(bridge.ResolvePath(g.CurrentMedia.Path));
            imgToVideo.LatentInput.ConnectTo(emptyVideoLatent.LATENT);
            bridge.AddNode(imgToVideo, "111");

            LTXVConcatAVLatentNode concat = new();
            concat.VideoLatent.ConnectTo(imgToVideo.Latent);
            concat.AudioLatent.ConnectTo(emptyAudioLatent.Latent);
            bridge.AddNode(concat, "113");

            LTXVSeparateAVLatentNode separate = new();
            separate.AvLatent.ConnectTo(concat.Latent);
            bridge.AddNode(separate, "201");

            VAEDecodeTiledNode videoDecode = new VAEDecodeTiledNode()
                .With(TileSize: 2048, Overlap: 256, TemporalSize: 64, TemporalOverlap: 16);
            videoDecode.Vae.ConnectToUntyped(videoVaeNode.GetOutput(0));
            videoDecode.Samples.ConnectTo(separate.VideoLatent);
            bridge.AddNode(videoDecode, "202");

            LTXVAudioVAEDecodeNode audioDecode = new();
            audioDecode.AudioVae.ConnectToUntyped(audioVaeNode.GetOutput(0));
            audioDecode.Samples.ConnectTo(separate.AudioLatent);
            bridge.AddNode(audioDecode, "203");

            SwarmSaveAnimationWSNode save = new SwarmSaveAnimationWSNode()
                .With(Fps: 24.0, Lossless: false, Quality: 95, Method: "default", Format: "h264-mp4");
            save.Images.ConnectTo(videoDecode.IMAGE);
            save.Audio.ConnectTo(audioDecode.Audio);
            bridge.AddNode(save, "9");

            g.CurrentMedia = videoDecode.IMAGE.ToWGMedia(g, WGNodeData.DT_VIDEO,
                width: 512, height: 512, frames: 16, fps: 24);
        }, 11);

    private static WorkflowGenerator.WorkflowGenStep SeedNativeAudioStep() =>
        new(g =>
        {
            using SyncingWorkflowBridge bridge = BridgeSync.For(g);

            UnknownNode audioSource = bridge.AddStub("UnitTest_AudioSource", "300").WithOutputs(WGNodeData.DT_AUDIO);

            if (g.CurrentMedia is not null)
            {
                g.CurrentMedia.AttachedAudio = audioSource.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_AUDIO);
            }
        }, 11.3);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRootLtxVideoChainStep(), SeedNativeAudioStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    [Fact]
    public void Injector_sets_empty_video_length_from_audio_length_frames_before_cleanup_sensitive_stages_run()
    {
        JObject workflow = [];
        using (WorkflowBridge buildBridge = WorkflowBridge.Create(workflow))
        {
            UnknownNode audioVae = buildBridge.AddStub("UnitTest_AudioVae", "105").WithOutputs(WGNodeData.DT_VAE);

            EmptyLTXVLatentVideoNode emptyVideoNode = new EmptyLTXVLatentVideoNode()
                .With(Length: 16, Width: 512, Height: 512, BatchSize: 1);
            buildBridge.AddNode(emptyVideoNode, "108");

            LTXVEmptyLatentAudioNode emptyAudioNode = new LTXVEmptyLatentAudioNode()
                .With(FramesNumber: 16, FrameRate: 24, BatchSize: 1);
            emptyAudioNode.AudioVae.ConnectToUntyped(audioVae.GetOutput(0));
            buildBridge.AddNode(emptyAudioNode, "109");

            LTXVConcatAVLatentNode concat = new();
            concat.VideoLatent.ConnectTo(emptyVideoNode.LATENT);
            concat.AudioLatent.ConnectTo(emptyAudioNode.Latent);
            buildBridge.AddNode(concat, "113");

            UnknownNode audioSource = buildBridge.AddStub("UnitTest_AudioSource", "300").WithOutputs(WGNodeData.DT_AUDIO);

            SaveAudioMP3Node save = new();
            save.Audio.ConnectToUntyped(audioSource.GetOutput(0));
            buildBridge.AddNode(save, "301");

            UnknownNode latentAudit = buildBridge.AddStub("UnitTest_LatentAudit", "400");
            latentAudit.GetInput("latent").ConnectToUntyped(emptyAudioNode.Latent);
        }

        WorkflowGenerator generator = CreateInjectorGenerator(workflow);
        WGNodeData audio = new(
            new JArray("300", 0),
            generator,
            WGNodeData.DT_AUDIO,
            generator.CurrentAudioVae?.Compat ?? generator.CurrentCompat());

        Assert.True(Runner.TryInjectLtxAudio(generator, audio));

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
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
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[]");

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
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

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

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

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadAudioB64Node uploadedAudioNode = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());

        LTXVAudioVAEEncodeNode audioEncode = Assert.Single(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Equal(uploadedAudioNode.Id, audioEncode.Audio.Connection!.Node.Id);
        Assert.Equal(0, audioEncode.Audio.Connection.SlotIndex);

        SetLatentNoiseMaskNode setMask = Assert.Single(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        Assert.Same(audioEncode.AudioLatent, setMask.Samples.Connection);

        IReadOnlyList<(ComfyNode Node, INodeInput Input)> maskConsumers =
            bridge.Graph.FindInputsConnectedTo(setMask.LATENT);
        Assert.Contains(maskConsumers, c => c.Input.Name == "audio_latent" && c.Node is LTXVConcatAVLatentNode);

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        ComfyNode saveAudioStart = saveNode.Audio.Connection!.Node;
        Assert.True(ReachesUpstream(bridge, saveAudioStart, uploadedAudioNode.Id));
        Assert.False(ReachesUpstream(bridge, saveAudioStart, "300"));
    }

    [Fact]
    public void Save_audio_stage_matches_video_length_to_uploaded_audio_when_enabled()
    {
        using SwarmUiTestContext _ = new();
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

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadAudioB64Node uploadedAudioNode = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());

        SwarmEnsureAudioNode lengthEnsure = Assert.IsType<SwarmEnsureAudioNode>(
            lengthToFrames.AudioInput.Connection!.Node);
        Assert.Same(uploadedAudioNode.AUDIO, lengthEnsure.Audio.Connection);
        Assert.NotEqual(uploadedAudioNode.Id, lengthToFrames.AudioInput.Connection.Node.Id);
        Assert.NotEqual("300", lengthToFrames.AudioInput.Connection.Node.Id);

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        ComfyNode saveAudioStart = saveNode.Audio.Connection!.Node;
        Assert.True(ReachesUpstream(bridge, saveAudioStart, uploadedAudioNode.Id));
        Assert.False(ReachesUpstream(bridge, saveAudioStart, "300"));
    }

    [Fact]
    public void Save_audio_stage_injects_audio_into_native_ltx_video_chain_before_stages_run()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(MakeClip(MakeStage(models.VideoModel.Name))).ToString();
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        Assert.Equal("300", lengthToFrames.AudioInput.Connection!.Node.Id);
        Assert.Equal(0, lengthToFrames.AudioInput.Connection.SlotIndex);

        LTXVAudioVAEEncodeNode audioEncode = Assert.Single(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Same(lengthToFrames.Audio, audioEncode.Audio.Connection);

        SetLatentNoiseMaskNode setMask = Assert.Single(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        IReadOnlyList<(ComfyNode Node, INodeInput Input)> maskConsumers =
            bridge.Graph.FindInputsConnectedTo(setMask.LATENT);
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
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = MakeRootConfig(
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

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<SwarmLoadAudioB64Node> uploadNodes = bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>();
        Assert.Equal(2, uploadNodes.Count);
    }

    [Fact]
    public void Multi_clip_parallel_merge_produces_batch_image_node_routing_to_g_current_media()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = MakeRootConfig(
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

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        BatchImagesNodeNode batchImagesNode = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        AudioConcatNode audioConcatNode = Assert.Single(bridge.Graph.NodesOfType<AudioConcatNode>());

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray(batchImagesNode.Id, 0)));

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);

        JObject batchInputs = (JObject)AsWorkflowNode(batchImagesNode, workflow).Node["inputs"];
        List<ComfyNode> batchImageStarts = batchInputs.Properties()
            .Where(p => p.Value is JArray { Count: 2 })
            .Select(p => bridge.Graph.GetNode($"{((JArray)p.Value)[0]}"))
            .Where(n => n is not null)
            .ToList();
        Assert.Equal(2, batchImageStarts.Count);
        foreach (ComfyNode start in batchImageStarts)
        {
            Assert.Contains(samplers, sampler => ReachesUpstream(bridge, start, sampler.Id));
        }
        Assert.NotEqual(batchImageStarts[0].Id, batchImageStarts[1].Id);

        ComfyNode audio1Start = audioConcatNode.Audio1.Connection!.Node;
        ComfyNode audio2Start = audioConcatNode.Audio2.Connection!.Node;
        Assert.Contains(samplers, sampler => ReachesUpstream(bridge, audio1Start, sampler.Id));
        Assert.Contains(samplers, sampler => ReachesUpstream(bridge, audio2Start, sampler.Id));

        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        Assert.True(JToken.DeepEquals(
            generator.CurrentMedia.AttachedAudio.Path,
            new JArray(audioConcatNode.Id, 0)));
    }

    [Fact]
    public void Save_audio_stage_uses_clip_uploaded_audio_when_switching_from_native_to_upload_clip()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = MakeRootConfig(
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

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadAudioB64Node uploadedAudioNode = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        IReadOnlyList<(ComfyNode Node, INodeInput Input)> uploadConsumers =
            bridge.Graph.FindInputsConnectedTo(uploadedAudioNode.AUDIO);
        Assert.Contains(uploadConsumers, c => c.Input.Name == "audio" && c.Node is SwarmEnsureAudioNode);
    }

    [Fact]
    public void Save_audio_stage_uses_root_stage_resolution_for_injected_audio_mask()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JObject
        {
            ["Width"] = 384,
            ["Height"] = 640,
            ["Clips"] = new JArray(
                MakeClip(MakeStage(models.VideoModel.Name)))
        }.ToString();
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SetLatentNoiseMaskNode setMask = Assert.Single(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        SolidMaskNode solidMask = Assert.IsType<SolidMaskNode>(setMask.Mask.Connection!.Node);

        Assert.Equal(384, solidMask.Width.LiteralAsInt());
        Assert.Equal(640, solidMask.Height.LiteralAsInt());
    }

}
