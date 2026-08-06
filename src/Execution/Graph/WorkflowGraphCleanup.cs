using ComfyTyped.Core;

namespace VideoStages.Execution.Graph;

/// <summary>
/// Every walk here reads its upstream neighbours through <see cref="ComfyGraph.FindUpstream"/>, not
/// <see cref="ComfyNode.Inputs"/>: autogrow list connections (a merged timeline's
/// <c>BatchImagesNode.images.imageN</c>) live in <see cref="ComfyNode.InputLists"/>, so iterating
/// <c>Inputs</c> would silently treat everything behind a multi-clip merge as unreachable and prune
/// loaders its decodes still read.
/// </summary>
internal static class WorkflowGraphCleanup
{
    public static IReadOnlySet<string> RemoveUnusedUpstreamNodes(
        WorkflowBridge bridge,
        string startNodeId,
        ISet<string> protectedNodeIds = null,
        IDictionary<string, string> nodeHelpers = null)
    {
        HashSet<string> removed = RemoveUnusedUpstreamNodesAndCollect(bridge, startNodeId, protectedNodeIds);
        VideoGraphHelpers.InvalidateForRemovedNodes(nodeHelpers, removed);
        return removed;
    }

    public static HashSet<string> RemoveUnusedUpstreamNodesAndCollect(
        WorkflowBridge bridge,
        string startNodeId,
        ISet<string> protectedNodeIds = null)
    {
        HashSet<string> removed = [];
        if (string.IsNullOrWhiteSpace(startNodeId))
        {
            return removed;
        }

        Queue<string> pending = new();
        HashSet<string> seen = [];
        pending.Enqueue(startNodeId);

        while (pending.Count > 0)
        {
            string nodeId = pending.Dequeue();
            if (string.IsNullOrWhiteSpace(nodeId)
                || !seen.Add(nodeId)
                || protectedNodeIds?.Contains(nodeId) == true)
            {
                continue;
            }

            ComfyNode node = bridge.Graph.GetNode(nodeId);
            if (node is null)
            {
                continue;
            }

            bool hasDownstreamConsumer = false;
            foreach (INodeOutput output in node.Outputs)
            {
                if (bridge.Graph.FindInputsConnectedTo(output).Any())
                {
                    hasDownstreamConsumer = true;
                    break;
                }
            }
            if (hasDownstreamConsumer)
            {
                continue;
            }

            List<string> upstreamIds = [.. bridge.Graph.FindUpstream(node).Select(up => up.Id)];
            bridge.RemoveNode(nodeId);
            removed.Add(nodeId);
            foreach (string upId in upstreamIds)
            {
                pending.Enqueue(upId);
            }
        }
        return removed;
    }

    public static void PruneNodesAddedSince(
        WorkflowBridge bridge,
        ISet<string> preservedNodeIds,
        IDictionary<string, string> nodeHelpers)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(preservedNodeIds);
        HashSet<string> removed = [];
        foreach (string nodeId in bridge.Graph.Nodes.Keys
            .Where(id => !preservedNodeIds.Contains(id))
            .ToArray())
        {
            removed.UnionWith(RemoveUnusedUpstreamNodesAndCollect(
                bridge,
                nodeId,
                preservedNodeIds));
        }
        VideoGraphHelpers.InvalidateForRemovedNodes(nodeHelpers, removed);
    }

    /// <summary>
    /// Collects the ids of every node connected to <paramref name="startNodeIds"/> in both
    /// directions, removing nothing.
    /// </summary>
    public static HashSet<string> CollectComponentIds(
        WorkflowBridge bridge, IEnumerable<string> startNodeIds) =>
        Traverse(bridge, startNodeIds, includeUpstream: true, includeDownstream: true);

    /// <summary>
    /// Captures only the disposable portion of a root-connected component. Any unrelated terminal
    /// sink (save, preview, external output) owns its complete upstream closure, so shared loaders
    /// and the branch behind them are excluded from the returned ownership set. The caller-supplied
    /// <paramref name="ownedTerminalNodeIds"/> are the root publications that may be retargeted or
    /// removed and therefore do not protect the displaced root.
    /// </summary>
    public static HashSet<string> CollectOwnedRootClosure(
        WorkflowBridge bridge,
        IEnumerable<string> rootNodeIds,
        IEnumerable<string> ownedTerminalNodeIds)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        HashSet<string> ownedTerminals = new(
            (ownedTerminalNodeIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)));
        HashSet<string> component = CollectComponentIds(bridge, rootNodeIds ?? []);
        HashSet<string> unrelatedTerminalIds = [
            .. bridge.Graph.Nodes.Values
                .Where(node => node.Outputs.Count == 0 && !ownedTerminals.Contains(node.Id))
                .Select(node => node.Id)
        ];
        HashSet<string> protectedNodes = CollectUpstreamClosure(bridge, unrelatedTerminalIds);
        component.ExceptWith(protectedNodes);
        return component;
    }

    /// <summary>
    /// Removes only nodes explicitly captured as owned, never expanding into adjacent graph
    /// branches. Current terminal sinks and caller-provided live roots protect their upstream
    /// dependencies, including shared loaders that acquired consumers after capture.
    /// <para>
    /// Returns what it removed, which is how far the timeline fell short of building on the host's
    /// own nodes: a stage that takes them over leaves nothing here to remove.
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> RemoveOwnedNodesNotLive(
        WorkflowBridge bridge,
        IEnumerable<string> ownedNodeIds,
        IEnumerable<string> liveRootNodeIds,
        IDictionary<string, string> nodeHelpers = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        HashSet<string> liveRootIds = new(
            (liveRootNodeIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)));
        liveRootIds.UnionWith(
            bridge.Graph.Nodes.Values
                .Where(node => node.Outputs.Count == 0)
                .Select(node => node.Id));
        HashSet<string> live = CollectUpstreamClosure(bridge, liveRootIds);
        HashSet<string> removed = [];
        foreach (string nodeId in (ownedNodeIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (live.Contains(nodeId) || bridge.Graph.GetNode(nodeId) is null)
            {
                continue;
            }
            bridge.RemoveNode(nodeId);
            removed.Add(nodeId);
        }
        VideoGraphHelpers.InvalidateForRemovedNodes(nodeHelpers, removed);
        return removed;
    }

    internal static HashSet<string> CollectUpstreamClosure(
        WorkflowBridge bridge, IEnumerable<string> rootNodeIds) =>
        Traverse(bridge, rootNodeIds, includeUpstream: true, includeDownstream: false);

    private static HashSet<string> Traverse(
        WorkflowBridge bridge,
        IEnumerable<string> startNodeIds,
        bool includeUpstream,
        bool includeDownstream)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        HashSet<string> seen = [];
        Queue<string> pending = new(
            (startNodeIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)));
        while (pending.Count > 0)
        {
            string nodeId = pending.Dequeue();
            if (!seen.Add(nodeId))
            {
                continue;
            }
            ComfyNode node = bridge.Graph.GetNode(nodeId);
            if (node is null)
            {
                continue;
            }
            if (includeUpstream)
            {
                foreach (ComfyNode upstream in bridge.Graph.FindUpstream(node))
                {
                    pending.Enqueue(upstream.Id);
                }
            }
            if (includeDownstream)
            {
                foreach (INodeOutput output in node.Outputs)
                {
                    foreach (var consumer in bridge.Graph.FindInputsConnectedTo(output))
                    {
                        pending.Enqueue(consumer.Node.Id);
                    }
                }
            }
        }
        return seen;
    }

}
