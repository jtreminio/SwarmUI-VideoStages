using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>
/// Core mints node ids two ways that share one id space: a running <c>LastID</c> counter, and
/// banded stable ids (LoRA loaders at 3000+, intermediate saves at 51000+). Only the banded
/// allocator checks whether an id is taken, so anything that drags <c>LastID</c> into a band makes
/// core's next unbanded node silently overwrite a banded one.
/// </summary>
[Collection("VideoStagesTests")]
public class HostNodeIdBandContractTests
{
    /// <summary>
    /// The graph that exposed it: a host LoRA parks a loader at 3000, and an alternate refiner
    /// model then wants a second loader at 3001 plus fresh conditioning. When the counter had been
    /// pushed to 3001 (by opening a typed bridge over the graph), the refiner's text encode landed
    /// on the refiner's own LoRA loader and the refiner sampler sampled off a CONDITIONING output.
    /// </summary>
    [Fact]
    public async Task An_alternate_refiner_model_keeps_its_own_lora_loader_beside_a_host_lora()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_HostLora.safetensors");
        T2IModel refiner = TestStubModel.Install(
            Program.T2IModelSets["Stable-Diffusion"],
            "UnitTest_Refiner.safetensors");
        refiner.ModelClass = fixture.BaseModel.ModelClass with { };

        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip), post =>
        {
            post["model"] = fixture.BaseModel.Name;
            post["refinermodel"] = refiner.Name;
            post["refinermethod"] = "PostApply";
            post["refinercontrolpercentage"] = 0.3;
            post["loras"] = new JArray("UnitTest_HostLora");
            post["loraweights"] = "1";
        });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LoraLoaderNode[] loras = [.. bridge.Graph.NodesOfType<LoraLoaderNode>()];
        Assert.Equal(2, loras.Length);
        Assert.Contains(fixture.BaseSampler(bridge).Model.Connection?.Node, loras);
        Assert.Contains(fixture.RefinerSampler(bridge).Model.Connection?.Node, loras);
    }
}
