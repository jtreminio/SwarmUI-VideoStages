using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using Xunit;

namespace VideoStages.Tests;

public class WorkflowGraphCleanupTests
{
    [Fact]
    public void RemoveUnusedUpstreamNodes_purges_stale_node_helper_entries()
    {
        JObject workflow = [];
        string upstreamId;
        string startId;
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            UnknownNode upstream = bridge.AddStub("UnitTest_EmptyAudio", "103").WithOutputs(WGNodeData.DT_LATENT_AUDIO);
            UnknownNode consumer = bridge.AddStub("UnitTest_Consumer", "104");
            consumer.GetInput("input").ConnectToUntyped(upstream.GetOutput(0));
            upstreamId = upstream.Id;
            startId = consumer.Id;
        }

        Dictionary<string, string> nodeHelpers = new()
        {
            ["__generic_node__UnitTest_EmptyAudio___{}"] = upstreamId,
            ["unrelated"] = "999",
        };

        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            WorkflowGraphCleanup.RemoveUnusedUpstreamNodes(bridge, startId, null, nodeHelpers);
        }

        Assert.False(workflow.ContainsKey(upstreamId));
        Assert.False(workflow.ContainsKey(startId));
        Assert.False(nodeHelpers.ContainsKey("__generic_node__UnitTest_EmptyAudio___{}"));
        Assert.Equal("999", nodeHelpers["unrelated"]);
    }
}
