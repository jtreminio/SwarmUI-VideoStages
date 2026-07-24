using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class RootVideoStageHandoff(WorkflowGenerator g, StageRefStore stageRefStore)
{
    private const string PreCoreNodeIdsKey = "videostages.pre-core-node-ids";

    public RootExecutionPolicy CreatePolicy() =>
        new(g.RequireVideoExecutionPlanContext().Plan);

    public bool ShouldHandoffRootStage()
    {
        return CreatePolicy().InterceptsHostCore;
    }

    public void CapturePreCoreVideoMedia()
    {
        if (!ShouldHandoffRootStage())
        {
            return;
        }
        stageRefStore.Capture(StageRefStore.StageKind.PreRootVideo);
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        g.NodeHelpers[PreCoreNodeIdsKey] = string.Join(",", bridge.Graph.Nodes.Keys);
    }

    public void DropCoreImageToVideoOutput()
    {
        StageRefStore.StageRef preRoot = stageRefStore.PreRootVideo;
        if (preRoot is null)
        {
            return;
        }

        try
        {
            g.CurrentMedia = preRoot.Media;
            if (preRoot.Vae is not null)
            {
                g.CurrentVae = preRoot.Vae;
            }
            PruneCoreImageToVideoNodes();
        }
        finally
        {
            stageRefStore.DiscardPreRootVideo();
            g.NodeHelpers.Remove(PreCoreNodeIdsKey);
        }
    }

    private void PruneCoreImageToVideoNodes()
    {
        if (!g.NodeHelpers.TryGetValue(PreCoreNodeIdsKey, out string snapshot))
        {
            return;
        }

        HashSet<string> preCoreIds = [.. snapshot.Split(',', StringSplitOptions.RemoveEmptyEntries)];
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        List<string> newIds = [.. bridge.Graph.Nodes.Keys.Where(id => !preCoreIds.Contains(id))];
        if (newIds.Count == 0)
        {
            return;
        }

        HashSet<string> removed = [];
        foreach (string newId in newIds)
        {
            foreach (string id in WorkflowGraphCleanup.RemoveUnusedUpstreamNodesAndCollect(bridge, newId, preCoreIds))
            {
                removed.Add(id);
            }
        }
        WorkflowGraphCleanup.InvalidateNodeHelperCacheForRemovedIds(g.NodeHelpers, removed);
    }
}
