using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.HostVideo.Runtime;

/// <summary>
/// A host-video architecture's root-media handoff. An architecture entering from host-owned root
/// media can capture the media before the host's image-to-video pass, restore it afterwards, and
/// prune everything the host added in between. Both host phases build a fresh participant, so the
/// capture lives in node helpers rather than in this object.
/// </summary>
internal sealed class HostVideoRootMediaHandoff
{
    private const string NullMarker = "<null>";
    private readonly WorkflowGenerator _generator;
    private readonly string _architectureDisplayLabel;
    private readonly string _preCoreMediaKey;
    private readonly string _preCoreVaeKey;
    private readonly string _preCoreNodeIdsKey;

    internal HostVideoRootMediaHandoff(
        WorkflowGenerator generator,
        ArchitectureId architectureId,
        string architectureDisplayLabel)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (string.IsNullOrWhiteSpace(architectureId.Value))
        {
            throw new ArgumentException(
                "Architecture id cannot be empty.",
                nameof(architectureId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(architectureDisplayLabel);
        _generator = generator;
        _architectureDisplayLabel = architectureDisplayLabel;
        string prefix = $"videostages.arch.{architectureId}";
        _preCoreMediaKey = $"{prefix}.pre-core.media";
        _preCoreVaeKey = $"{prefix}.pre-core.vae";
        _preCoreNodeIdsKey = $"{prefix}.pre-core-node-ids";
    }

    internal void CapturePreCoreMedia()
    {
        if (!new RootExecutionPolicy(
                _generator.RequireVideoExecutionPlanContext().Plan).InterceptsHostCore)
        {
            Cleanup();
            return;
        }
        Cleanup();
        try
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
            string media = EncodeRequiredMarker(
                bridge,
                _generator.CurrentMedia,
                "host root media");
            string vae = _generator.CurrentVae is null
                ? NullMarker
                : EncodeRequiredMarker(bridge, _generator.CurrentVae, "host root VAE");
            string snapshot = string.Join(",", bridge.Graph.Nodes.Keys);
            if (string.IsNullOrWhiteSpace(snapshot))
            {
                throw HandoffError("the pre-core workflow snapshot is empty");
            }

            // Publish only after every captured value has been validated, so a failed capture
            // cannot leave a partial handoff that looks usable to the post-core phase.
            _generator.NodeHelpers[_preCoreMediaKey] = media;
            _generator.NodeHelpers[_preCoreVaeKey] = vae;
            _generator.NodeHelpers[_preCoreNodeIdsKey] = snapshot;
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    internal void DropCoreOutput()
    {
        if (!new RootExecutionPolicy(
                _generator.RequireVideoExecutionPlanContext().Plan).InterceptsHostCore)
        {
            Cleanup();
            return;
        }

        try
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
            WGNodeData vae = LoadCapturedVae(bridge);
            WGNodeData media = LoadRequiredMarker(
                bridge,
                _preCoreMediaKey,
                "host root media",
                fallbackVae: vae);
            HashSet<string> preCoreIds = LoadRequiredSnapshot(bridge);
            _generator.CurrentMedia = media;
            _generator.CurrentVae = vae;
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
        WorkflowGraphCleanup.InvalidateNodeHelperCacheForRemovedIds(
            _generator.NodeHelpers,
            removed);
    }

    /// <summary>
    /// Deliberately narrower than LTX's stage-ref marker: this handoff only intercepts host root
    /// image media today, so it carries no frame count, frame rate, or attached audio to preserve.
    /// </summary>
    private string EncodeRequiredMarker(
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
        if (!_generator.NodeHelpers.TryGetValue(_preCoreVaeKey, out string encoded))
        {
            throw HandoffError("the captured host root VAE state is missing");
        }
        return encoded == NullMarker
            ? null
            : LoadRequiredMarker(
                bridge,
                _preCoreVaeKey,
                "host root VAE",
                fallbackVae: null);
    }

    private WGNodeData LoadRequiredMarker(
        WorkflowBridge bridge,
        string key,
        string description,
        WGNodeData fallbackVae)
    {
        if (!_generator.NodeHelpers.TryGetValue(key, out string encoded)
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
            _generator,
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
        if (!_generator.NodeHelpers.TryGetValue(_preCoreNodeIdsKey, out string snapshot)
            || string.IsNullOrWhiteSpace(snapshot))
        {
            throw HandoffError("the pre-core workflow snapshot is missing");
        }
        string[] ids = snapshot.Split(',', StringSplitOptions.None);
        if (ids.Length == 0
            || ids.Any(string.IsNullOrWhiteSpace)
            || ids.Any(id => bridge.Graph.GetNode(id) is null))
        {
            throw HandoffError(
                "the pre-core workflow snapshot is malformed or references removed nodes");
        }
        return [.. ids];
    }

    private void Cleanup()
    {
        VideoGraphHelpers.RemoveCached(_generator, _preCoreMediaKey);
        VideoGraphHelpers.RemoveCached(_generator, _preCoreVaeKey);
        VideoGraphHelpers.RemoveCached(_generator, _preCoreNodeIdsKey);
    }

    private SwarmUserErrorException HandoffError(string detail) =>
        new(
            $"VideoStages: {_architectureDisplayLabel} could not restore the host root because "
            + $"{detail}.");

    private static int? Nullable(string value) =>
        int.TryParse(value, out int parsed) ? parsed : null;
}
