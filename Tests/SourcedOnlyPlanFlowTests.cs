using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Plan_backed_sourced_only_ltx_clip_publishes_conformed_footage_without_a_sampler()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject sourced = MakeSourcedClip(models);
        sourced["Stages"] = new JArray();

        (JObject workflow, WorkflowGenerator generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = AssertSourcedConformChain(bridge);
        Assert.Empty(SamplerNodesOrdered(bridge));
        INodeOutput currentOutput = bridge.ResolvePath((JArray)generator.CurrentMedia.Path);
        Assert.True(ReachesUpstream(bridge, currentOutput.Node, window.Id));
        AssertWorkflowHasNoCycles(workflow);
    }
}
