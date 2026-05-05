using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal sealed class RootVideoStageHandoff(WorkflowGenerator g, JsonParser jsonParser, StageRefStore stageRefStore)
{
    private const string SynthesizedRootVideoModelKey = "videostages.synth-root-video-model";
    private const string PreCoreNodeIdsKey = "videostages.pre-core-node-ids";

    public static bool IsTextToVideoRootWorkflow(WorkflowGenerator g)
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel existingVideoModel)
            && existingVideoModel is not null)
        {
            return false;
        }
        return g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true;
    }

    public bool ShouldReplaceTextToVideoRootStage(JsonParser.StageSpec stage)
    {
        return stage is not null
            && stage.ClipStageIndex == 0
            && IsTextToVideoRootWorkflow(g);
    }

    public void EnsureRootVideoStageModel()
    {
        if (HasNativeVideoModel())
        {
            return;
        }
        if (IsTextToVideoRootWorkflow(g))
        {
            return;
        }

        JsonParser.StageSpec firstStage = jsonParser.ParseStages().FirstOrDefault();
        if (firstStage is null || string.IsNullOrWhiteSpace(firstStage.Model))
        {
            return;
        }

        g.UserInput.Set(T2IParamTypes.VideoModel.Type, firstStage.Model);
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel _))
        {
            g.NodeHelpers[SynthesizedRootVideoModelKey] = "1";
            return;
        }

        g.UserInput.Remove(T2IParamTypes.VideoModel);
        Logs.Warning($"VideoStages: could not resolve root video model '{firstStage.Model}'.");
    }

    public void CleanupSynthesizedRootVideoStageModel()
    {
        if (g.NodeHelpers.Remove(SynthesizedRootVideoModelKey))
        {
            g.UserInput.Remove(T2IParamTypes.VideoModel);
        }
    }

    public bool ShouldHandoffRootStage()
    {
        if (VideoStagesExtension.CoreImageToVideoStep is null)
        {
            return false;
        }
        bool hasNativeVideoModel = HasNativeVideoModel();
        bool hasTextToVideoRootModel = IsTextToVideoRootWorkflow(g);
        if (!hasNativeVideoModel && !hasTextToVideoRootModel)
        {
            return false;
        }
        if (hasNativeVideoModel && !WorkflowGenerator.Steps.Contains(VideoStagesExtension.CoreImageToVideoStep))
        {
            return false;
        }
        return jsonParser.ParseStages().Count > 0;
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

    private bool HasNativeVideoModel()
    {
        return g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel _);
    }
}
