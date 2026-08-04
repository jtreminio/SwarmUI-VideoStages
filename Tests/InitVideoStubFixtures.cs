using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>
/// What is left of the init-video stub-harness suite after its tests moved to
/// <see cref="Ltx2InitVideoContractTests"/>: the clip shapes and the seeded raw text-to-video host
/// state that <c>TypedExecutionFlowTests</c> and <c>RootOutputOwnershipTests</c> still build on.
/// Delete this file when those two convert.
/// </summary>
public partial class StageFlowTests
{
    private const double InitVideoClipDuration = 0.6;
    private const double InitVideoStartSeconds = 1.0;

    private static readonly string[] InitVideoClipFeatures =
        [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    private static JObject MakeInitVideoClip(TestModelBundle models)
    {
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        ((JObject)((JArray)clip["stages"])[0]).Remove("imageReference");
        clip["duration"] = InitVideoClipDuration;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64," + Convert.ToBase64String([0x11, 0x22, 0x33]),
            ["fileName"] = "footage.mp4",
            ["startSeconds"] = InitVideoStartSeconds
        };
        return clip;
    }

    private static JObject MakeGeneratedClip(TestModelBundle models)
    {
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        clip["duration"] = InitVideoClipDuration;
        return clip;
    }

    private static (JObject Workflow, WorkflowGenerator Generator) GenerateInitVideoFlow(
        TestModelBundle models, params JObject[] clips)
    {
        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, MakeRootConfig(512, 512, clips).ToString());
        return WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: true),
            features: InitVideoClipFeatures);
    }

    // Mirrors the REAL host graph at VideoStages time for a text-to-video root: the raw AV latent
    // is still undecoded — the real core priority-10 step (CorePreVideoSavePrepStep) then authors
    // the separate/decode/save itself, leaving CurrentMedia.AttachedAudio as a LATENT audio ref.
    // The harness seeds that author decode+save wholesale never reproduce that state.
    private static WorkflowGenerator.WorkflowGenStep SeedRawTextToVideoAvLatentRootStep() =>
        new(g =>
        {
            T2IModel model = g.UserInput.Get(T2IParamTypes.Model, null);
            g.FinalLoadedModel = model;
            g.FinalLoadedModelList = model is null ? [] : [model];

            using var bridge = BridgeSync.For(g);
            UnknownNode unet = bridge.AddStub("UnitTest_RootUnet", "4").WithOutputs(WGNodeData.DT_MODEL, "CLIP");
            g.CurrentModel = unet.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);
            g.CurrentTextEnc = unet.GetOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC);
            UnknownNode vaeLoader = bridge.AddStub("UnitTest_RootVae", "101").WithOutputs(WGNodeData.DT_VAE);
            g.CurrentVae = vaeLoader.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_VAE);
            UnknownNode audioVaeLoader = bridge.AddStub("UnitTest_RootAudioVae", "102").WithOutputs(WGNodeData.DT_VAE);
            g.CurrentAudioVae = audioVaeLoader.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_AUDIOVAE);

            UnknownNode emptyVideo = bridge.AddStub("UnitTest_EmptyVideoLatent", "5").WithOutputs("LATENT");
            UnknownNode emptyAudio = bridge.AddStub("UnitTest_EmptyAudioLatent", "103").WithOutputs("LATENT");
            LTXVConcatAVLatentNode concat = new();
            concat.VideoLatent.ConnectToUntyped(emptyVideo.GetOutput(0));
            concat.AudioLatent.ConnectToUntyped(emptyAudio.GetOutput(0));
            bridge.AddNode(concat, "104");
            SwarmKSamplerNode sampler = bridge.AddNode(new SwarmKSamplerNode(), "10");
            sampler.Model.ConnectToUntyped(unet.GetOutput(0));
            sampler.LatentImage.ConnectTo(concat.Latent);

            // A dead consumer pinning the root latent — the live flows grow these transiently
            // (the root's audio-decode sibling, detached guide decodes). An upstream-only prune
            // stops at the sampler because of it; the dead-component sweep must remove both.
            LTXVSeparateAVLatentNode strayDetach = new();
            strayDetach.AvLatent.ConnectTo(sampler.LATENT);
            bridge.AddNode(strayDetach, "11");

            g.CurrentMedia = new WGNodeData(
                new JArray("10", 0), g, WGNodeData.DT_LATENT_AUDIOVIDEO, T2IModelClassSorter.CompatLtxv2);
        }, 4);
}
