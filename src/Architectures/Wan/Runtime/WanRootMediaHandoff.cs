using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Wan's host-root handoff. A Wan clip entering from the host image-to-video root compiles to
/// <see cref="Planning.HostCoreDisposition"/>.Handoff: the host still runs its own image-to-video
/// pass, so the image it started from is captured before that happens, restored afterwards, and
/// everything the host added in between is pruned. Wan then generates from that image itself.
/// Both host phases build a fresh adapter, so the capture lives in node helpers rather than in
/// this object.
/// </summary>
internal sealed class WanRootMediaHandoff(WorkflowGenerator g)
{
    private readonly WanRuntimeKeyScope _keys = new();

    internal void CapturePreCoreMedia()
    {
        if (!new RootExecutionPolicy(g.RequireVideoExecutionPlanContext().Plan).InterceptsHostCore)
        {
            return;
        }
        StoreMarker(_keys.PreCoreMedia, g.CurrentMedia);
        StoreMarker(_keys.PreCoreVae, g.CurrentVae);
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        g.NodeHelpers[_keys.PreCoreNodeIds] = string.Join(",", bridge.Graph.Nodes.Keys);
    }

    internal void DropCoreOutput()
    {
        if (!g.NodeHelpers.ContainsKey(_keys.PreCoreMedia))
        {
            return;
        }
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        WGNodeData vae = LoadMarker(bridge, _keys.PreCoreVae, fallbackVae: null);
        WGNodeData media = LoadMarker(bridge, _keys.PreCoreMedia, fallbackVae: vae);
        if (media is null)
        {
            return;
        }

        try
        {
            g.CurrentMedia = media;
            if (vae is not null)
            {
                g.CurrentVae = vae;
            }
            PruneCoreNodes(bridge);
        }
        finally
        {
            VideoGraphHelpers.RemoveCached(g, _keys.PreCoreMedia);
            VideoGraphHelpers.RemoveCached(g, _keys.PreCoreVae);
            VideoGraphHelpers.RemoveCached(g, _keys.PreCoreNodeIds);
        }
    }

    private void PruneCoreNodes(WorkflowBridge bridge)
    {
        if (!g.NodeHelpers.TryGetValue(_keys.PreCoreNodeIds, out string snapshot))
        {
            return;
        }
        HashSet<string> preCoreIds = [.. snapshot.Split(',', StringSplitOptions.RemoveEmptyEntries)];
        HashSet<string> removed = [];
        foreach (string newId in bridge.Graph.Nodes.Keys
            .Where(id => !preCoreIds.Contains(id))
            .ToArray())
        {
            removed.UnionWith(WorkflowGraphCleanup.RemoveUnusedUpstreamNodesAndCollect(
                bridge,
                newId,
                preCoreIds));
        }
        WorkflowGraphCleanup.InvalidateNodeHelperCacheForRemovedIds(g.NodeHelpers, removed);
    }

    /// <summary>
    /// Deliberately narrower than LTX's stage-ref marker: the host root Wan enters from is a still
    /// image, so it carries no frame count, frame rate, or attached audio to preserve.
    /// </summary>
    private void StoreMarker(string key, WGNodeData data)
    {
        if (data?.Path is not JArray { Count: 2 } path)
        {
            VideoGraphHelpers.RemoveCached(g, key);
            return;
        }
        VideoGraphHelpers.CacheMarker(g, key, [
            $"{path[0]}",
            $"{path[1]}",
            data.DataType ?? WGNodeData.DT_IMAGE,
            data.Width.HasValue ? $"{data.Width.Value}" : "",
            data.Height.HasValue ? $"{data.Height.Value}" : "",
            data.Compat?.ID ?? ""]);
    }

    private WGNodeData LoadMarker(WorkflowBridge bridge, string key, WGNodeData fallbackVae)
    {
        if (!g.NodeHelpers.TryGetValue(key, out string encoded)
            || string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }
        string[] parts = encoded.Split(VideoGraphHelpers.MarkerSeparator);
        if (parts.Length < 6 || !int.TryParse(parts[1], out int slot))
        {
            return null;
        }
        if (bridge.Graph.GetNode(parts[0])?.FindOutput(slot) is not INodeOutput output)
        {
            Logs.Warning(
                $"VideoStages: Wan pre-core node '{parts[0]}' slot {slot} is no longer in the "
                + "workflow; treating the host root as not captured.");
            return null;
        }
        return WGNodeDataMarkerCodec.Build(
            g,
            output,
            parts[2],
            parts[5],
            fallbackVae,
            Nullable(parts[3]),
            Nullable(parts[4]),
            frames: null,
            fps: null);
    }

    private static int? Nullable(string value) =>
        int.TryParse(value, out int parsed) ? parsed : null;
}
