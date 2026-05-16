using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

public readonly record struct WorkflowNode(string Id, JObject Node);

internal static class TypedWorkflowAssertions
{
    public static List<SwarmKSamplerNode> SamplerNodesOrdered(WorkflowBridge bridge)
    {
        return bridge.Graph.NodesOfType<SwarmKSamplerNode>()
            .OrderBy(n => int.Parse(n.Id))
            .ToList();
    }

    public static List<ComfyNode> LoraLoaderNodesOf(WorkflowBridge bridge)
    {
        return bridge.Graph.Nodes.Values
            .Where(n => n is LoraLoaderNode or LoraLoaderModelOnlyNode)
            .ToList();
    }

    public static T RequireTypedNode<T>(WorkflowBridge bridge, string id) where T : ComfyNode
    {
        T node = bridge.Graph.GetNode<T>(id);
        Assert.NotNull(node);
        return node;
    }

    public static WorkflowNode AsWorkflowNode(ComfyNode node, JObject workflow)
    {
        Assert.True(workflow[node.Id] is JObject, $"Expected workflow to contain node id '{node.Id}'.");
        return new WorkflowNode(node.Id, (JObject)workflow[node.Id]);
    }

    /// <summary>
    /// Inclusive variant of <see cref="ComfyGraph.IsReachableUpstream"/>: returns true if
    /// <paramref name="start"/> IS the target node, or if <paramref name="targetNodeId"/>
    /// is reachable by walking upstream from it.
    /// </summary>
    public static bool ReachesUpstream(WorkflowBridge bridge, ComfyNode start, string targetNodeId) =>
        start is not null
        && (start.Id == targetNodeId || bridge.Graph.IsReachableUpstream(start, targetNodeId));
}
