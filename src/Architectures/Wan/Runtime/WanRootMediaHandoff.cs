using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Wan's host-root handoff. A Wan clip entering from the host image-to-video root compiles to
/// <see cref="HostCoreDisposition"/>.Handoff: the host still runs its own image-to-video
/// pass, so the image it started from is captured before that happens, restored afterwards, and
/// everything the host added in between is pruned. Wan then generates from that image itself.
/// Both host phases build a fresh adapter, so the capture lives in node helpers rather than in
/// this object.
/// </summary>
internal sealed class WanRootMediaHandoff(WorkflowGenerator g)
{
    private const string NullMarker = "<null>";
    private readonly WanRuntimeKeyScope _keys = new();

    internal void CapturePreCoreMedia()
    {
        if (!new RootExecutionPolicy(g.RequireVideoExecutionPlanContext().Plan).InterceptsHostCore)
        {
            Cleanup();
            return;
        }
        Cleanup();
        try
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            string media = EncodeRequiredMarker(bridge, g.CurrentMedia, "host root image");
            string vae = g.CurrentVae is null
                ? NullMarker
                : EncodeRequiredMarker(bridge, g.CurrentVae, "host root VAE");
            string snapshot = string.Join(",", bridge.Graph.Nodes.Keys);
            if (string.IsNullOrWhiteSpace(snapshot))
            {
                throw HandoffError("the pre-core workflow snapshot is empty");
            }

            // Publish only after every captured value has been validated, so a failed capture
            // cannot leave a partial handoff that looks usable to the post-core phase.
            g.NodeHelpers[_keys.PreCoreMedia] = media;
            g.NodeHelpers[_keys.PreCoreVae] = vae;
            g.NodeHelpers[_keys.PreCoreNodeIds] = snapshot;
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    internal void DropCoreOutput()
    {
        if (!new RootExecutionPolicy(g.RequireVideoExecutionPlanContext().Plan).InterceptsHostCore)
        {
            Cleanup();
            return;
        }

        try
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            WGNodeData vae = LoadCapturedVae(bridge);
            WGNodeData media = LoadRequiredMarker(
                bridge,
                _keys.PreCoreMedia,
                "host root image",
                fallbackVae: vae);
            HashSet<string> preCoreIds = LoadRequiredSnapshot(bridge);
            g.CurrentMedia = media;
            g.CurrentVae = vae;
            PruneCoreNodes(bridge, preCoreIds);
        }
        finally
        {
            Cleanup();
        }
    }

    private void PruneCoreNodes(WorkflowBridge bridge, HashSet<string> preCoreIds)
    {
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
    private static string EncodeRequiredMarker(
        WorkflowBridge bridge,
        WGNodeData data,
        string description)
    {
        if (data?.Path is not JArray { Count: 2 } path
            || path[1].Type != JTokenType.Integer
            || bridge.ResolvePath(path) is null)
        {
            throw HandoffError($"{description} is missing or no longer resolves in the workflow");
        }
        return string.Join(VideoGraphHelpers.MarkerSeparator, [
            $"{path[0]}",
            $"{path[1]}",
            data.DataType ?? WGNodeData.DT_IMAGE,
            data.Width.HasValue ? $"{data.Width.Value}" : "",
            data.Height.HasValue ? $"{data.Height.Value}" : "",
            data.Compat?.ID ?? ""]);
    }

    private WGNodeData LoadCapturedVae(WorkflowBridge bridge)
    {
        if (!g.NodeHelpers.TryGetValue(_keys.PreCoreVae, out string encoded))
        {
            throw HandoffError("the captured host root VAE state is missing");
        }
        return encoded == NullMarker
            ? null
            : LoadRequiredMarker(bridge, _keys.PreCoreVae, "host root VAE", fallbackVae: null);
    }

    private WGNodeData LoadRequiredMarker(
        WorkflowBridge bridge,
        string key,
        string description,
        WGNodeData fallbackVae)
    {
        if (!g.NodeHelpers.TryGetValue(key, out string encoded)
            || string.IsNullOrWhiteSpace(encoded))
        {
            throw HandoffError($"the captured {description} marker is missing");
        }
        string[] parts = encoded.Split(VideoGraphHelpers.MarkerSeparator);
        if (parts.Length != 6
            || string.IsNullOrWhiteSpace(parts[0])
            || !int.TryParse(parts[1], out int slot)
            || slot < 0)
        {
            throw HandoffError($"the captured {description} marker is malformed");
        }
        if (bridge.Graph.GetNode(parts[0])?.FindOutput(slot) is not INodeOutput output)
        {
            throw HandoffError(
                $"the captured {description} node '{parts[0]}' output {slot} was removed");
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

    private HashSet<string> LoadRequiredSnapshot(WorkflowBridge bridge)
    {
        if (!g.NodeHelpers.TryGetValue(_keys.PreCoreNodeIds, out string snapshot)
            || string.IsNullOrWhiteSpace(snapshot))
        {
            throw HandoffError("the pre-core workflow snapshot is missing");
        }
        string[] ids = snapshot.Split(',', StringSplitOptions.None);
        if (ids.Length == 0
            || ids.Any(string.IsNullOrWhiteSpace)
            || ids.Any(id => bridge.Graph.GetNode(id) is null))
        {
            throw HandoffError("the pre-core workflow snapshot is malformed or references removed nodes");
        }
        return [.. ids];
    }

    private void Cleanup()
    {
        VideoGraphHelpers.RemoveCached(g, _keys.PreCoreMedia);
        VideoGraphHelpers.RemoveCached(g, _keys.PreCoreVae);
        VideoGraphHelpers.RemoveCached(g, _keys.PreCoreNodeIds);
    }

    private static SwarmUserErrorException HandoffError(string detail) =>
        new($"VideoStages: Wan could not restore the host root because {detail}.");

    private static int? Nullable(string value) =>
        int.TryParse(value, out int parsed) ? parsed : null;
}
