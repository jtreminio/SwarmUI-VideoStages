using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Finds the host ControlNet apply node that belongs to a configured model.</summary>
internal sealed class ControlNetGraphDiscovery(WorkflowGenerator g)
{
    private static readonly (string ApplyClass, string LoaderInputName)[] KnownApplyNodes =
    [
        (ControlNetApplyAdvancedNode.ClassType, "control_net"),
        (ControlNetInpaintingAliMamaApplyNode.ClassType, "control_net"),
        (QwenImageDiffsynthControlnetNode.ClassType, "model_patch"),
    ];

    public bool TryFindCoreApply(
        WorkflowBridge bridge,
        T2IModel controlModel,
        ISet<string> usedApplyNodes,
        out (string Id, JObject Node) applyNode,
        out JArray fullControlImage)
    {
        applyNode = default;
        fullControlImage = null;
        string controlModelName = controlModel.ToString(g.ModelFolderFormat);
        foreach ((string applyClass, string loaderInputName) in KnownApplyNodes)
        {
            IEnumerable<ComfyNode> candidates = bridge.Graph.NodesOfType(applyClass)
                .OrderBy(node => int.TryParse(node.Id, out int id) ? id : int.MaxValue);
            foreach (ComfyNode candidate in candidates)
            {
                if (usedApplyNodes.Contains(candidate.Id)
                    || g.Workflow[candidate.Id] is not JObject candidateNode
                    || !VideoGraphHelpers.TryGetInputRef(candidateNode, loaderInputName, out JArray loaderRef)
                    || !VideoGraphHelpers.TryGetInputRef(candidateNode, "image", out JArray imageInput)
                    || !LoaderChainContainsModel(bridge, loaderRef, controlModelName))
                {
                    continue;
                }
                applyNode = (candidate.Id, candidateNode);
                fullControlImage = imageInput;
                return true;
            }
        }
        return false;
    }

    private static bool LoaderChainContainsModel(
        WorkflowBridge bridge,
        JArray loaderRef,
        string controlModelName)
    {
        if (bridge.ResolvePath(loaderRef)?.Node is not ComfyNode start)
        {
            return false;
        }
        return LoaderMatches(start, controlModelName)
            || LoaderMatches(bridge.Graph.FindNearestUpstream<ControlNetLoaderNode>(start), controlModelName)
            || LoaderMatches(bridge.Graph.FindNearestUpstream<ModelPatchLoaderNode>(start), controlModelName);
    }

    private static bool LoaderMatches(ComfyNode node, string controlModelName) => node switch
    {
        ControlNetLoaderNode cn => cn.ControlNetName.LiteralAsString() == controlModelName,
        ModelPatchLoaderNode mp => mp.Name.LiteralAsString() == controlModelName,
        _ => false,
    };
}
