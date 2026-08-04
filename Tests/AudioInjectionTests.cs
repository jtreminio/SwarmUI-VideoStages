using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using VideoStages.Architectures.Ltx2;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>
/// What is left of the stub-harness audio suite: the two warning paths whose audio sources cannot
/// be seeded from a POST, one direct-API handoff check, and the seconds rounding the timeline
/// cannot produce. The graph contracts live in <see cref="Ltx2AudioContractTests"/>.
/// </summary>
[Collection("VideoStagesTests")]
public class AudioInjectionTests
{
    private static JObject MakeStage(string model) => new()
    {
        ["control"] = 1.0,
        ["upscale"] = 1.0,
        ["upscaleMethod"] = "pixel-lanczos",
        ["model"] = model,
        ["steps"] = 10,
        ["cfgScale"] = 4.5,
        ["sampler"] = "euler",
        ["scheduler"] = "normal",
        ["imageReference"] = "Generated"
    };

    private static JObject MakeClipConfig(string audioSource, params JObject[] stages) => new()
    {
        ["audioSource"] = audioSource,
        ["stages"] = new JArray(stages)
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

            using WorkflowBridge bridge = BridgeSync.For(g);

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
                .With(FramesNumber: 16, FrameRate: "24", BatchSize: 1);
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
            using WorkflowBridge bridge = BridgeSync.For(g);

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

    /// <summary>
    /// The comfy node reads the window list as seconds at two decimals, so the builder rounds. Half
    /// must go away from zero, not to even: banker's rounding would move 5.555 to 5.55 and shorten
    /// the preserved window. No POST-reachable document is known to produce a .xx5 tie, so the
    /// rounding rule is only observable by calling the builder.
    /// </summary>
    [Fact]
    public void Preserve_window_seconds_round_half_away_from_zero()
    {
        JObject workflow = [];
        using (WorkflowBridge buildBridge = WorkflowBridge.Create(workflow))
        {
            _ = buildBridge.AddStub("UnitTest_AudioVae", "105").WithOutputs(WGNodeData.DT_VAE);
            _ = buildBridge.AddStub("UnitTest_AudioLatent", "310")
                .WithOutputs(WGNodeData.DT_LATENT_AUDIO);
        }

        WorkflowGenerator generator = CreateInjectorGenerator(workflow);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SwarmSetAudioMaskWindowsNode mask = AudioPreserveWindowBuilder.AddMask(
            generator,
            bridge,
            new JArray("310", 0),
            [(0.0, 1.0 / 3.0), (4.004, 5.555)],
            stableIdSlot: 0);

        JArray windows = JArray.Parse(mask.Windows.LiteralAsString());
        Assert.Equal(2, windows.Count);
        Assert.Equal(0.0, (double)windows[0]["start"]);
        Assert.Equal(0.33, (double)windows[0]["end"]);
        Assert.Equal(4.0, (double)windows[1]["start"]);
        Assert.Equal(5.56, (double)windows[1]["end"]);
    }

    [Fact]
    public void Missing_selected_ace_audio_track_warns_and_continues_without_it()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(MakeClipConfig("audio7", MakeStage(models.VideoModel.Name))).ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());

        Assert.NotEmpty(workflow);
        List<string> warnings = Assert.IsType<List<string>>(input.ExtraMeta["parser_warnings"]);
        Assert.Contains(
            warnings,
            warning => warning.Contains("audio7")
                && warning.Contains("continuing without that source"));
    }

    [Fact]
    public void Missing_selected_controlnet_audio_capture_warns_and_uses_silence()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject clip = MakeClipConfig(
            Constants.AudioSourceControlNet,
            MakeStage(models.VideoModel.Name));
        clip["icLoras"] = new JArray(new JObject
        {
            ["lora"] = "unused-control",
            ["driveSource"] = Constants.ControlNetSourceOne,
            ["driveData"] = $"{IcLoraDriveData.Visual}",
        });
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(clip).ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildSteps());

        Assert.NotEmpty(workflow);
        List<string> warnings = Assert.IsType<List<string>>(input.ExtraMeta["parser_warnings"]);
        Assert.Contains(
            warnings,
            warning => warning.Contains("ControlNet 1 audio") && warning.Contains("using silence"));
    }

    [Fact]
    public void Decoded_stage_audio_handoff_reuses_native_latent_without_encode_mask_cycle()
    {
        JObject workflow = [];
        using (WorkflowBridge buildBridge = WorkflowBridge.Create(workflow))
        {
            _ = buildBridge.AddStub("UnitTest_VideoVae", "104")
                .WithOutputs(WGNodeData.DT_VAE);
            UnknownNode audioVae = buildBridge.AddStub("UnitTest_AudioVae", "105")
                .WithOutputs(WGNodeData.DT_VAE);
            UnknownNode avLatent = buildBridge.AddStub("UnitTest_PriorStageAvLatent", "200")
                .WithOutputs(WGNodeData.DT_LATENT_AUDIOVIDEO);

            LTXVSeparateAVLatentNode separate = new();
            separate.AvLatent.ConnectToUntyped(avLatent.GetOutput(0));
            buildBridge.AddNode(separate, "201");

            LTXVAudioVAEDecodeNode decode = new();
            decode.AudioVae.ConnectToUntyped(audioVae.GetOutput(0));
            decode.Samples.ConnectTo(separate.AudioLatent);
            buildBridge.AddNode(decode, "203");

            _ = buildBridge.AddStub("UnitTest_NextStageVideoLatent", "204")
                .WithOutputs(WGNodeData.DT_LATENT_VIDEO);
        }

        WorkflowGenerator generator = CreateInjectorGenerator(workflow);
        generator.CurrentVae = new WGNodeData(
            new JArray("104", 0),
            generator,
            WGNodeData.DT_VAE,
            T2IModelClassSorter.CompatLtxv2);
        WGNodeData nextStageVideoLatent = new(
            new JArray("204", 0),
            generator,
            WGNodeData.DT_LATENT_VIDEO,
            T2IModelClassSorter.CompatLtxv2)
        {
            AttachedAudio = new WGNodeData(
                new JArray("203", 0),
                generator,
                WGNodeData.DT_AUDIO,
                T2IModelClassSorter.CompatLtxv2)
        };

        WGNodeData normalized = LtxDecodedAudioHandoff.PreferNativeLatent(
            generator,
            nextStageVideoLatent);
        WGNodeData samplingLatent = normalized.AsSamplingLatent(
            generator.CurrentVae,
            generator.CurrentAudioVae);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        Assert.Equal(WGNodeData.DT_LATENT_AUDIO, normalized.AttachedAudio.DataType);
        Assert.True(JToken.DeepEquals(
            normalized.AttachedAudio.Path,
            new JArray("201", 1)));

        LTXVConcatAVLatentNode concat = Assert.IsType<LTXVConcatAVLatentNode>(
            bridge.ResolvePath(samplingLatent.Path).Node);
        Assert.Equal("201", concat.AudioLatent.Connection!.Node.Id);
        Assert.Equal(1, concat.AudioLatent.Connection.SlotIndex);
        Assert.Empty(bridge.Graph.NodesOfType<SwarmEnsureAudioNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
    }
}
