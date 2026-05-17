using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class StageRunnerRetargetTests
{
    [Fact]
    public void RetargetExistingAnimationSaves_decodes_latent_attached_audio_through_current_audio_vae()
    {
        // Ensures static registrations (T2IParamTypes etc.) are populated before constructing WorkflowGenerator.
        _ = WorkflowTestHarness.VideoStagesSteps();

        // Mirrors the user-reported scenario: CurrentMedia.AttachedAudio points to a LATENT-typed output
        // (LTXVSeparateAVLatent.audio_latent on the LTX-2 path). Pre-fix, the retargeter wired that LATENT
        // straight into SwarmSaveAnimationWS.audio (which wants DT_AUDIO), throwing
        // "Cannot connect output of type 'LATENT' to input 'audio' of type 'AUDIO'."
        JObject workflow = [];
        using (WorkflowBridge build = WorkflowBridge.Create(workflow))
        {
            UnknownNode videoVae = build.AddStub("UnitTest_VideoVae", "104").WithOutputs(WGNodeData.DT_VAE);
            build.AddStub("UnitTest_AudioVae", "105").WithOutputs(WGNodeData.DT_VAE);
            UnknownNode avLatentSource = build.AddStub("UnitTest_AvLatent", "200").WithOutputs("LATENT");

            LTXVSeparateAVLatentNode separate = new();
            separate.AvLatent.ConnectToUntyped(avLatentSource.GetOutput(0));
            build.AddNode(separate, "201");

            VAEDecodeTiledNode oldDecode = new VAEDecodeTiledNode().With(
                TileSize: 2048,
                Overlap: 256,
                TemporalSize: 64,
                TemporalOverlap: 16);
            oldDecode.Vae.ConnectToUntyped(videoVae.GetOutput(0));
            oldDecode.Samples.ConnectTo(separate.VideoLatent);
            build.AddNode(oldDecode, "202");

            build.AddStub("UnitTest_NewProducer", "210").WithOutputs(WGNodeData.DT_VIDEO);

            SwarmSaveAnimationWSNode save = new SwarmSaveAnimationWSNode().With(
                Fps: 24.0,
                Lossless: false,
                Quality: 95,
                Method: "default",
                Format: "h264-mp4");
            save.Images.ConnectTo(oldDecode.IMAGE);
            build.AddNode(save, "9");
        }

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.VideoFPS, 24);

        T2IModel videoModel = new(null!, "", "", "unit-ltxv2")
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
            FinalLoadedModel = videoModel,
            FinalLoadedModelList = [videoModel]
        };
        generator.CurrentAudioVae = new WGNodeData(
            new JArray("105", 0), generator, WGNodeData.DT_AUDIOVAE, T2IModelClassSorter.CompatLtxv2);
        generator.CurrentMedia = new WGNodeData(
            new JArray("210", 0), generator, WGNodeData.DT_VIDEO, T2IModelClassSorter.CompatLtxv2);
        generator.CurrentMedia.AttachedAudio = new WGNodeData(
            new JArray("201", 1), generator, WGNodeData.DT_LATENT_AUDIO, T2IModelClassSorter.CompatLtxv2);

        StageRunner runner = new(generator, null, null, null);
        runner.RetargetExistingAnimationSaves(
            new JArray("202", 0),
            new JArray("210", 0),
            retargetAudio: true);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());

        Assert.Equal("210", saveNode.Images.Connection!.Node.Id);

        LTXVAudioVAEDecodeNode audioDecode = Assert.IsType<LTXVAudioVAEDecodeNode>(saveNode.Audio.Connection!.Node);
        Assert.Equal("201", audioDecode.Samples.Connection!.Node.Id);
        Assert.Equal(1, audioDecode.Samples.Connection.SlotIndex);
    }
}
