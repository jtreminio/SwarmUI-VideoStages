using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

public readonly record struct WorkflowNode(string Id, JObject Node);

internal static class TypedWorkflowAssertions
{
    /// <summary>Fails when the graph has a dependency cycle (Comfy refuses to run such a workflow).</summary>
    public static void AssertAcyclic(WorkflowBridge bridge)
    {
        Dictionary<string, int> state = [];
        void Visit(ComfyNode node)
        {
            if (state.TryGetValue(node.Id, out int seen))
            {
                if (seen == 1)
                {
                    Assert.Fail($"Workflow contains a cycle through node {node.Id} ({node.ClassTypeName}).");
                }
                return;
            }
            state[node.Id] = 1;
            foreach (INodeInput input in node.Inputs)
            {
                if (input.Connection?.Node is ComfyNode upstream)
                {
                    Visit(upstream);
                }
            }
            state[node.Id] = 2;
        }
        foreach (ComfyNode node in bridge.Graph.Nodes.Values)
        {
            Visit(node);
        }
    }

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
