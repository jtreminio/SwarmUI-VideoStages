using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using VideoStages.Architectures.Ltx2;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// What is left of the stub-harness audio suite: one direct-API handoff check and the seconds
/// rounding the timeline cannot produce. The graph contracts live in
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
