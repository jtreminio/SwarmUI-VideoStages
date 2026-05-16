using ComfyTyped.Core;

namespace VideoStages;

internal static class WorkflowGraphCleanup
{
    public static void RemoveUnusedUpstreamNodes(
        WorkflowBridge bridge,
        string startNodeId,
        ISet<string> protectedNodeIds = null)
    {
        _ = RemoveUnusedUpstreamNodesAndCollect(bridge, startNodeId, protectedNodeIds);
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
