using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal sealed class RootMediaHandoff(
    WorkflowGenerator generator,
    string architectureDisplayLabel)
{
    private Snapshot _snapshot;

    internal bool ShouldHandoffRootStage() =>
        new RootExecutionPolicy(
            generator.RequireVideoExecutionPlanContext().Plan).InterceptsHostCore;

    internal void CapturePreCoreMedia()
    {
        if (!ShouldHandoffRootStage())
        {
            _snapshot = null;
            return;
        }

        _snapshot = null;
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        try
        {
            RequireResolvable(bridge, generator.CurrentMedia, "host root media");
            if (generator.CurrentVae is not null)
            {
                RequireResolvable(bridge, generator.CurrentVae, "host root VAE");
            }
            HashSet<string> preCoreNodeIds = [.. bridge.Graph.Nodes.Keys];
            if (preCoreNodeIds.Count == 0)
            {
                throw HandoffError("the pre-core workflow snapshot is empty");
            }
            _snapshot = new(
                generator.CurrentMedia,
                generator.CurrentVae,
                preCoreNodeIds);
        }
        catch
        {
            _snapshot = null;
            throw;
        }
    }

    internal void DropCoreOutput()
    {
        if (!ShouldHandoffRootStage())
        {
            _snapshot = null;
            return;
        }

        try
        {
            Snapshot snapshot = _snapshot
                ?? throw HandoffError("the captured root state is missing");
            using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
            RequireResolvable(bridge, snapshot.Media, "host root media");
            if (snapshot.Vae is not null)
            {
                RequireResolvable(bridge, snapshot.Vae, "host root VAE");
            }
            if (snapshot.PreCoreNodeIds.Any(id => bridge.Graph.GetNode(id) is null))
            {
                throw HandoffError(
                    "the pre-core workflow snapshot references removed nodes");
            }

            generator.CurrentMedia = snapshot.Media;
            generator.CurrentVae = snapshot.Vae;
            WorkflowGraphCleanup.PruneNodesAddedSince(
                bridge,
                snapshot.PreCoreNodeIds,
                generator.NodeHelpers);
        }
        finally
        {
            _snapshot = null;
        }
    }

    private void RequireResolvable(
        WorkflowBridge bridge,
        WGNodeData data,
        string description)
    {
        if (data?.Path is not JArray { Count: 2 } path
            || path[1].Type != JTokenType.Integer
            || bridge.ResolvePath(path) is null)
        {
            throw HandoffError(
                $"{description} is missing or no longer resolves in the workflow");
        }
    }

    private InvalidOperationException HandoffError(string detail) =>
        VideoStagesInvariant.Failure(
            $"{architectureDisplayLabel} could not restore the host root because {detail}.");

    private sealed record Snapshot(
        WGNodeData Media,
        WGNodeData Vae,
        HashSet<string> PreCoreNodeIds);
}
