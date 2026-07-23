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
    /// Removes the dead subgraph around <paramref name="startNodeIds"/>: every node connected to
    /// them (walking BOTH directions) that no save/output depends on. The plain upstream walk
    /// stops at any node with a residual consumer — but a replaced root generation can be held
    /// alive by consumers that are themselves dead (an audio-decode sibling, a detached guide
    /// chain), leaving a dangling sampler no later cleanup can remove. Liveness — the upstream
    /// closure of <paramref name="liveRootNodeIds"/> — is the boundary: expansion never crosses
    /// into or removes it, so shared loaders survive, and a root whose latent a stage genuinely
    /// reuses is itself live.
    /// </summary>
    public static void RemoveDeadComponentAround(
        WorkflowBridge bridge,
        IEnumerable<string> startNodeIds,
        IEnumerable<string> liveRootNodeIds,
        IDictionary<string, string> nodeHelpers = null)
    {
        HashSet<string> live = CollectUpstreamClosure(bridge, liveRootNodeIds);
        HashSet<string> seen = [];
        List<string> toRemove = [];
        Queue<string> pending = new(startNodeIds.Where(id => !string.IsNullOrWhiteSpace(id)));
        while (pending.Count > 0)
        {
            string nodeId = pending.Dequeue();
            if (!seen.Add(nodeId) || live.Contains(nodeId))
            {
                continue;
            }
            ComfyNode node = bridge.Graph.GetNode(nodeId);
            if (node is null)
            {
                continue;
            }
            toRemove.Add(nodeId);
            foreach (INodeInput input in node.Inputs)
            {
                if (input.Connection?.Node?.Id is string upId)
                {
                    pending.Enqueue(upId);
                }
            }
            foreach (INodeOutput output in node.Outputs)
            {
                foreach (var consumer in bridge.Graph.FindInputsConnectedTo(output))
                {
                    pending.Enqueue(consumer.Node.Id);
                }
            }
        }

        HashSet<string> removed = [];
        foreach (string nodeId in toRemove)
        {
            bridge.RemoveNode(nodeId);
            removed.Add(nodeId);
        }
        InvalidateNodeHelperCacheForRemovedIds(nodeHelpers, removed);
    }

    /// <summary>
    /// Collects the ids of every node connected to <paramref name="startNodeIds"/> (walking BOTH
    /// directions), removing nothing. For capturing a doomed component BEFORE partial cleanups
    /// delete the seed nodes — the ids feed a later RemoveDeadComponentAround, whose liveness
    /// boundary then spares whatever the surviving graph still depends on.
    /// </summary>
    public static HashSet<string> CollectComponentIds(
        WorkflowBridge bridge, IEnumerable<string> startNodeIds)
    {
        HashSet<string> seen = [];
        Queue<string> pending = new(startNodeIds.Where(id => !string.IsNullOrWhiteSpace(id)));
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
            foreach (INodeInput input in node.Inputs)
            {
                if (input.Connection?.Node?.Id is string upId)
                {
                    pending.Enqueue(upId);
                }
            }
            foreach (INodeOutput output in node.Outputs)
            {
                foreach (var consumer in bridge.Graph.FindInputsConnectedTo(output))
                {
                    pending.Enqueue(consumer.Node.Id);
                }
            }
        }
        return seen;
    }

    private static HashSet<string> CollectUpstreamClosure(
        WorkflowBridge bridge, IEnumerable<string> rootNodeIds)
    {
        HashSet<string> seen = [];
        Queue<string> pending = new(rootNodeIds.Where(id => !string.IsNullOrWhiteSpace(id)));
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
            foreach (INodeInput input in node.Inputs)
            {
                if (input.Connection?.Node?.Id is string upId)
                {
                    pending.Enqueue(upId);
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
