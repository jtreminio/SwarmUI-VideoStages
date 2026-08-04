using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class WorkflowGraphCleanupTests
{
    [Fact]
    public void Graph_traversal_distinguishes_connected_component_from_upstream_closure()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode upstream = bridge.AddStub("UnitTest_Source", "100")
            .WithOutputs(WGNodeData.DT_IMAGE);
        UnknownNode middle = bridge.AddStub("UnitTest_Middle", "101")
            .WithOutputs(WGNodeData.DT_IMAGE);
        UnknownNode downstream = bridge.AddStub("UnitTest_Sink", "102");
        _ = bridge.AddStub("UnitTest_Unrelated", "199");
        middle.GetInput("input").ConnectToUntyped(upstream.GetOutput(0));
        downstream.GetInput("input").ConnectToUntyped(middle.GetOutput(0));

        HashSet<string> component =
            WorkflowGraphCleanup.CollectComponentIds(bridge, [middle.Id]);
        HashSet<string> upstreamOnly =
            WorkflowGraphCleanup.CollectUpstreamClosure(bridge, [middle.Id]);

        Assert.True(component.SetEquals([upstream.Id, middle.Id, downstream.Id]));
        Assert.True(upstreamOnly.SetEquals([upstream.Id, middle.Id]));
    }

    /// <summary>
    /// A multi-clip timeline reaches its save through a BatchImagesNode, whose per-image
    /// connections are autogrow list children rather than singular inputs. Traversals that read
    /// only <c>Inputs</c> stop dead at that node, so shared loaders behind the merge read as
    /// unreachable and get pruned out from under the decodes still wired to them.
    /// </summary>
    [Fact]
    public void Graph_traversal_crosses_autogrow_list_connections()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode loader = bridge.AddStub("UnitTest_Vae", "101").WithOutputs(WGNodeData.DT_VAE);
        UnknownNode decode = bridge.AddStub("UnitTest_Decode", "200")
            .WithOutputs(WGNodeData.DT_IMAGE);
        decode.GetInput("vae").ConnectToUntyped(loader.GetOutput(0));
        BatchImagesNodeNode batch = bridge.AddNode(new BatchImagesNodeNode(), "300");
        batch.Images.AddFromUntyped(decode.GetOutput(0));

        Assert.True(
            WorkflowGraphCleanup.CollectUpstreamClosure(bridge, [batch.Id])
                .SetEquals([batch.Id, decode.Id, loader.Id]));
        WorkflowGraphCleanup.RemoveOwnedNodesNotLive(bridge, [loader.Id, decode.Id], [batch.Id]);
        Assert.True(workflow.ContainsKey(loader.Id));
        Assert.True(workflow.ContainsKey(decode.Id));
    }

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
