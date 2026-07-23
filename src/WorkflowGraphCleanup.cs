using ComfyTyped.Core;

namespace VideoStages;

internal static class WorkflowGraphCleanup
{
    public static void RemoveUnusedUpstreamNodes(
        WorkflowBridge bridge,
        string startNodeId,
        ISet<string> protectedNodeIds = null,
        IDictionary<string, string> nodeHelpers = null)
    {
        HashSet<string> removed = RemoveUnusedUpstreamNodesAndCollect(bridge, startNodeId, protectedNodeIds);
        InvalidateNodeHelperCacheForRemovedIds(nodeHelpers, removed);
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

            List<string> upstreamIds = [];
            foreach (INodeInput input in node.Inputs)
            {
                string upId = input.Connection?.Node?.Id;
                if (!string.IsNullOrWhiteSpace(upId))
                {
                    upstreamIds.Add(upId);
                }
            }

            bridge.RemoveNode(nodeId);
            removed.Add(nodeId);
            foreach (string upId in upstreamIds)
            {
                pending.Enqueue(upId);
            }
        }
        return removed;
    }

    /// <summary>
    /// Collects the ids of every node connected to <paramref name="startNodeIds"/> (walking BOTH
    /// directions), removing nothing.
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
    /// </summary>
    public static void RemoveOwnedNodesNotLive(
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
        InvalidateNodeHelperCacheForRemovedIds(nodeHelpers, removed);
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
                foreach (INodeInput input in node.Inputs)
                {
                    if (input.Connection?.Node?.Id is string upstreamId)
                    {
                        pending.Enqueue(upstreamId);
                    }
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

    public static void InvalidateNodeHelperCacheForRemovedIds(
        IDictionary<string, string> nodeHelpers,
        IReadOnlyCollection<string> removedNodeIds)
    {
        if (nodeHelpers is null || removedNodeIds is null || removedNodeIds.Count == 0)
        {
            return;
        }

        List<string> staleKeys = [];
        foreach (KeyValuePair<string, string> entry in nodeHelpers)
        {
            if (!string.IsNullOrEmpty(entry.Value) && removedNodeIds.Contains(entry.Value))
            {
                staleKeys.Add(entry.Key);
            }
        }
        foreach (string key in staleKeys)
        {
            nodeHelpers.Remove(key);
        }
    }
}
