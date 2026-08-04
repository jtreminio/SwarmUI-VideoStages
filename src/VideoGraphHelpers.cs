using ComfyTyped.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

/// <summary>
/// Provides the common <c>NodeHelpers</c> accessors/codecs and is the sole owner of invalidation
/// caused by graph-node removal. Invalidation recognizes the extension's bare node id, JSON
/// <c>[nodeId, slot]</c> path, and pipe-delimited <c>nodeId|slot|datatype|...</c> marker plus
/// SwarmUI's <c>modelId:modelSlot:clipId:clipSlot:vaeId:vaeSlot</c> loader tuple.
/// Architecture-scoped snapshots and richer marker payload codecs stay beside their consumers,
/// but every graph-removal path routes its removed ids through <see cref="InvalidateForRemovedNodes"/>
/// (normally through <see cref="RemoveNode"/> or <see cref="WorkflowGraphCleanup"/>), preventing
/// stale cache entries from surviving removed nodes.
/// </summary>
internal static class VideoGraphHelpers
{
    private const string ModelLoaderKeyPrefix = "modelloader_";

    /// <summary>Every cache key this extension writes, and no key SwarmUI writes.</summary>
    public const string ExtensionKeyPrefix = "videostages.";

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

    /// <summary>
    /// Records what happened to a set of nodes, rather than a reference to them. Each entry is
    /// <c>id=class</c> and every entry carries a trailing comma, including the only one: without it
    /// a single-entry record would read as a bare node reference, and
    /// <see cref="InvalidateForRemovedNodes"/> would delete the record of a removal when that very
    /// node was removed.
    /// </summary>
    public static void CacheRemovalRecord(
        WorkflowGenerator g,
        string key,
        IEnumerable<KeyValuePair<string, string>> removals) =>
        g.NodeHelpers[key] = string.Concat(
            removals.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}={entry.Value},"));

    /// <summary>Reads back a <see cref="CacheRemovalRecord"/> as node id to class.</summary>
    public static IReadOnlyDictionary<string, string> ReadRemovalRecord(
        WorkflowGenerator g,
        string key) =>
        (g.NodeHelpers.GetValueOrDefault(key) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : "");

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

    /// <summary>
    /// True when one of this extension's own captures still names one of
    /// <paramref name="nodeIds"/>. Rewriting such a node in place — rather than removing it —
    /// would hand whoever captured it the replacement instead, silently, so callers that overwrite
    /// a host node must refuse when this holds. SwarmUI's own entries are deliberately not
    /// consulted: they describe the root chain, which is what an overwrite supersedes.
    /// </summary>
    public static bool IsCapturedByExtension(
        IDictionary<string, string> nodeHelpers,
        IReadOnlyCollection<string> nodeIds) =>
        nodeHelpers is not null
        && nodeHelpers.Any(entry =>
            entry.Key.StartsWith(ExtensionKeyPrefix, StringComparison.Ordinal)
            && ReferencesRemovedNode(entry.Key, entry.Value, nodeIds));

    /// <summary>Drops every cache entry whose value references one of
    /// <paramref name="removedNodeIds"/>, in any supported extension or host encoding.</summary>
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
            if (ReferencesRemovedNode(entry.Key, entry.Value, removedNodeIds))
            {
                staleKeys.Add(entry.Key);
            }
        }
        foreach (string key in staleKeys)
        {
            nodeHelpers.Remove(key);
        }
    }

    private static bool ReferencesRemovedNode(
        string key,
        string encoded,
        IReadOnlyCollection<string> removedNodeIds)
    {
        if (key.StartsWith(ModelLoaderKeyPrefix, StringComparison.Ordinal))
        {
            return TryParseModelLoaderNodeIds(encoded, out IReadOnlyList<string> nodeIds)
                && nodeIds.Any(removedNodeIds.Contains);
        }
        return ReferencedNodeId(encoded) is string nodeId
            && removedNodeIds.Contains(nodeId);
    }

    /// <summary>
    /// Parses only SwarmUI's exact six-part model-loader cache format. The model and CLIP pairs
    /// are required; the VAE pair must be wholly absent or wholly valid. Malformed host values are
    /// opaque rather than guessed at during destructive graph cleanup.
    /// </summary>
    private static bool TryParseModelLoaderNodeIds(
        string encoded,
        out IReadOnlyList<string> nodeIds)
    {
        nodeIds = [];
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }
        string[] parts = encoded.Split(':');
        if (parts.Length != 6
            || !TryParseNodeSlotPair(parts[0], parts[1], required: true, out string modelId)
            || !TryParseNodeSlotPair(parts[2], parts[3], required: true, out string clipId)
            || !TryParseNodeSlotPair(parts[4], parts[5], required: false, out string vaeId))
        {
            return false;
        }
        nodeIds = new[] { modelId, clipId, vaeId }
            .Where(id => id is not null)
            .ToArray();
        return true;
    }

    private static bool TryParseNodeSlotPair(
        string nodeId,
        string slot,
        bool required,
        out string parsedNodeId)
    {
        parsedNodeId = null;
        bool nodeMissing = string.IsNullOrWhiteSpace(nodeId);
        bool slotMissing = string.IsNullOrWhiteSpace(slot);
        if (!required && nodeMissing && slotMissing)
        {
            return true;
        }
        if (nodeMissing || slotMissing || !int.TryParse(slot, out _))
        {
            return false;
        }
        parsedNodeId = nodeId;
        return true;
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
