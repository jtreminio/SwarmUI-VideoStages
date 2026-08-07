using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution.Audio;
using VideoStages.Generated;
using Xunit;
using VideoStages.Architectures.Ltx2.Runtime;
using VideoStages.Architectures.Ltx2.Runtime.Audio;

namespace VideoStages.Tests;

/// <summary>
/// Audio behaviour that generated graphs cannot reach: the injector's replace path, the direct-API
/// handoff, and the seconds rounding. The graph contracts live in
/// <see cref="Ltx2AudioContractTests"/>.
/// </summary>
[Collection("VideoStagesTests")]
public class AudioInjectionTests
{
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

    private static JObject BuildAudioInjectionWorkflow()
    {
        return new JObject
        {
            ["1"] = new JObject
            {
                ["class_type"] = SwarmKSamplerNode.ClassType,
                ["inputs"] = new JObject { ["seed"] = 42 }
            },
            ["2"] = new JObject
            {
                ["class_type"] = LTXVEmptyLatentAudioNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["frames_number"] = 97,
                    ["frame_rate"] = 24,
                    ["batch_size"] = 1,
                    ["audio_vae"] = new JArray("10", 0)
                }
            },
            ["3"] = new JObject
            {
                ["class_type"] = LTXVConcatAVLatentNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["video_latent"] = new JArray("1", 0),
                    ["audio_latent"] = new JArray("2", 0)
                }
            }
        };
    }

    /// <summary>
    /// <c>matchVideoLengthToAudio</c> makes the uploaded track decide the video length: the empty
    /// video latent's frame count stops being a literal and becomes a wire off
    /// <c>SwarmAudioLengthToFrames</c>, and the audio the model conditions on is that same node's
    /// output, so the two cannot drift. Disabling the whole branch used to leave the suite green.
    /// </summary>
    [Fact]
    public void Length_matching_wires_the_video_frame_count_to_the_audio_length()
    {
        JObject workflow = BuildAudioInjectionWorkflow();
        workflow["4"] = new JObject
        {
            ["class_type"] = EmptyLTXVLatentVideoNode.ClassType,
            ["inputs"] = new JObject
            {
                ["width"] = 512,
                ["height"] = 512,
                ["length"] = 97,
                ["batch_size"] = 1
            }
        };
        // A second consumer keeps the empty audio latent connected, so it survives the prune and
        // its retargeted frame count stays observable. Without it every empty audio node is
        // removed and the audio half of the retargeting cannot be seen at all.
        workflow["5"] = new JObject
        {
            ["class_type"] = "UnitTest_AudioLatentConsumer",
            ["inputs"] = new JObject { ["samples"] = new JArray("2", 0) }
        };
        WorkflowGenerator generator = CreateInjectorGenerator(workflow);
        WGNodeData audio = new(
            new JArray("901", 0),
            generator,
            WGNodeData.DT_AUDIO,
            T2IModelClassSorter.CompatLtxv2);

        bool injected = new LtxAudioInjector(generator, new RootVideoStageResizer(generator))
            .TryInject(audio, matchVideoLengthToAudio: true);

        Assert.True(injected);
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());
        // The timeline rate, not the node's codegen default.
        Assert.Equal(24, lengthToFrames.FrameRate.LiteralAsInt());
        EmptyLTXVLatentVideoNode video = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Same(lengthToFrames.Frames, video.Length.Connection);
        LTXVAudioVAEEncodeNode encode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Same(lengthToFrames.Audio, encode.Audio.Connection);
        LTXVEmptyLatentAudioNode survivor = Assert.Single(
            bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>());
        Assert.Same(lengthToFrames.Frames, survivor.FramesNumber.Connection);
    }

    /// <summary>No generated-graph test reaches <c>TryInject</c> past its empty-concat guard, so
    /// this stub harness is the only thing that exercises the injector at all.</summary>
    [Fact]
    public void Injection_replaces_the_model_supplied_empty_audio_latent()
    {
        WorkflowGenerator generator = CreateInjectorGenerator(BuildAudioInjectionWorkflow());
        WGNodeData audio = new(
            new JArray("901", 0),
            generator,
            WGNodeData.DT_AUDIO,
            T2IModelClassSorter.CompatLtxv2);

        bool injected = new LtxAudioInjector(generator, new RootVideoStageResizer(generator))
            .TryInject(audio, matchVideoLengthToAudio: false);

        Assert.True(injected);
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>());
        SetLatentNoiseMaskNode mask = Assert.Single(
            bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>());
        LTXVConcatAVLatentNode concat = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(mask.LATENT, concat.AudioLatent.Connection);
    }

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
