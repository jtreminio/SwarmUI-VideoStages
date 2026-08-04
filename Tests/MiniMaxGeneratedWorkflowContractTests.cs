using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxGeneratedWorkflowContractTests
{
    [Fact]
    public async Task Basic_text_to_video_can_be_generated_from_the_Comfy_API_POST_body()
    {
        using SwarmUiTestContext context = new(clearModelGenSteps: false);
        TestModelFactory.InstallMiniMaxSupportModels();
        using SafetensorsModelFixture modelFixture = SafetensorsModelFixture.Create(
            "models/diffusion_models/MiniMaxH3-Workflow-Test.safetensors");
        T2IModel videoModel = modelFixture.Model;
        Program.T2IModelSets["Stable-Diffusion"] = modelFixture.Handler;

        JObject clip = MakeClip(
            MakeStage(videoModel.Name, "Generated", steps: 8, cfgScale: 1));
        clip["duration"] = 1.0;
        JObject postParameters = new()
        {
            ["prompt"] = "A red kite flying over a green field",
            ["seed"] = 1,
            ["width"] = 512,
            ["height"] = 512,
            ["model"] = videoModel.Name,
            ["automaticvae"] = true,
            ["text2videoframes"] = 25,
            ["videostagesenabled"] = true,
            ["videostages"] = MakeDocument(clip).ToString(),
        };

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(postParameters);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        SwarmKSamplerNode sampler = Assert.Single(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Same(latent, sampler.LatentImage.Connection?.Node);
        Assert.Single(bridge.Graph.NodesOfType<VAEDecodeAudioNode>());
    }
}
