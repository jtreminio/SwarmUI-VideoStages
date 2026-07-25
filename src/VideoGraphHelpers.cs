using ComfyTyped.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

/// <summary>
/// The sole owner of the <c>NodeHelpers</c> node-reference cache: every write, read and
/// invalidation of a cached node id goes through here. Invalidation understands all three
/// encodings VideoStages actually stores - a bare node id (SwarmUI's own convention), a JSON
/// <c>[nodeId, slot]</c> path, and the pipe-delimited <c>nodeId|slot|datatype|...</c> marker - so a
/// removed node can never leave a live-looking cache entry behind (the "Node N not found" class of
/// failure).
/// </summary>
internal static class VideoGraphHelpers
{
    /// <summary>The pipe-delimited marker separator, shared with the marker writers.</summary>
    internal const char MarkerSeparator = '|';

    /// <summary>Reads a bridge-resolvable [nodeId, slot] path cached in NodeHelpers
    /// under <paramref name="key"/>; false when absent or no longer resolvable.</summary>
    public static bool TryGetCachedPath(
        WorkflowGenerator g, WorkflowBridge bridge, string key, out JArray path)
    {
        if (g.NodeHelpers.TryGetValue(key, out string encoded)
            && !string.IsNullOrWhiteSpace(encoded)
            && JToken.Parse(encoded) is JArray { Count: 2 } cached
            && (bridge is null || bridge.ResolvePath(cached) is not null))
        {
            path = cached;
            return true;
        }
        path = null;
        return false;
    }

    public static void CachePath(WorkflowGenerator g, string key, JArray path) =>
        g.NodeHelpers[key] = path.ToString(Formatting.None);

    public static void CacheMarker(WorkflowGenerator g, string key, IEnumerable<string> parts) =>
        g.NodeHelpers[key] = string.Join(MarkerSeparator, parts);

    public static bool RemoveCached(WorkflowGenerator g, string key) =>
        g.NodeHelpers.Remove(key);

    /// <summary>Removes <paramref name="nodeId"/> from the graph and drops every cache entry that
    /// still points at it. Removal sites must use this instead of a bare
    /// <c>bridge.RemoveNode</c>.</summary>
    public static void RemoveNode(WorkflowGenerator g, WorkflowBridge bridge, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return;
        }
        bridge.RemoveNode(nodeId);
        InvalidateForRemovedNodes(g?.NodeHelpers, [nodeId]);
    }

    /// <summary>Drops every cache entry whose value references one of
    /// <paramref name="removedNodeIds"/>, in any encoding VideoStages writes.</summary>
    public static void InvalidateForRemovedNodes(
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
            if (ReferencedNodeId(entry.Value) is string nodeId
                && removedNodeIds.Contains(nodeId))
            {
                staleKeys.Add(entry.Key);
            }
        }
        foreach (string key in staleKeys)
        {
            nodeHelpers.Remove(key);
        }
    }

    /// <summary>The node id a cached value refers to, or null when the value is not a node
    /// reference (for example the comma-joined pre-core id snapshot).</summary>
    private static string ReferencedNodeId(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }
        if (encoded.IndexOf(MarkerSeparator) >= 0)
        {
            string[] parts = encoded.Split(MarkerSeparator);
            return parts.Length >= 2 && int.TryParse(parts[1], out _) ? parts[0] : null;
        }
        if (encoded.StartsWith('['))
        {
            try
            {
                return JToken.Parse(encoded) is JArray { Count: 2 } path ? $"{path[0]}" : null;
            }
            catch (JsonReaderException)
            {
                return null;
            }
        }
        return encoded.Contains(',') ? null : encoded;
    }

    public static string StripDataUriPrefix(string data)
    {
        int comma = data.IndexOf(',');
        return data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
            ? data[(comma + 1)..]
            : data;
    }

    public static bool TryGetInputRef(JObject node, string inputName, out JArray inputRef)
    {
        inputRef = null;
        if (node["inputs"] is not JObject inputs
            || !inputs.TryGetValue(inputName, out JToken token)
            || token is not JArray { Count: 2 } array)
        {
            return false;
        }
        inputRef = array;
        return true;
    }
}
