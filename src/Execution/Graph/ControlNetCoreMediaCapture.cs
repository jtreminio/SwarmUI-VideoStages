using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Execution.Graph;

/// <summary>Captures each active core video ControlNet's host image, apply node and audio.</summary>
internal sealed class ControlNetCoreMediaCapture(WorkflowGenerator g)
{
    private const string CapturedMarkerKey = "videostages.controlnet.captured";
    private const string ImagePrefix = "videostages.controlnet.fullimage.";
    private const string AudioPrefix = "videostages.controlnet.audio.";
    private const string ApplyPrefix = "videostages.controlnet.apply.";
    private static readonly (string ApplyClass, string LoaderInputName)[] KnownApplyNodes =
    [
        (ControlNetApplyAdvancedNode.ClassType, "control_net"),
        (ControlNetInpaintingAliMamaApplyNode.ClassType, "control_net"),
        (QwenImageDiffsynthControlnetNode.ClassType, "model_patch"),
        ("AnimaLLLiteApply", "model_patch"),
    ];

    internal static IEnumerable<int> Indices =>
        Enumerable.Range(0, T2IParamTypes.Controlnets.Length);

    public void Capture()
    {
        if (!g.NodeHelpers.TryAdd(CapturedMarkerKey, "captured"))
        {
            return;
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        HashSet<string> usedApplyNodes = [];
        foreach (int index in Indices)
        {
            T2IParamTypes.ControlNetParamHolder parameters = T2IParamTypes.Controlnets[index];
            if (parameters is null
                || !g.UserInput.TryGet(parameters.Strength, out double _)
                || !g.UserInput.TryGet(parameters.Model, out T2IModel model)
                || !TryFindCoreApply(
                    bridge,
                    model,
                    usedApplyNodes,
                    out string applyId,
                    out JArray controlImage))
            {
                Clear(index);
                continue;
            }

            VideoGraphHelpers.CachePath(
                g,
                ImageKey(index),
                new JArray(controlImage[0], controlImage[1]));
            g.NodeHelpers[ApplyKey(index)] = applyId;
            CaptureUpstreamAudio(bridge, controlImage, index);
            usedApplyNodes.Add(applyId);
        }
    }

    /// <summary>Resolves the cached capture against the live graph: a captured node that a later
    /// cleanup removed must read as "not captured", never as a dangling reference.</summary>
    internal bool TryGetCapturedControlImage(
        int index,
        out WGNodeData controlImage)
    {
        controlImage = null;
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (!IsValidIndex(index)
            || !VideoGraphHelpers.TryGetCachedPath(
                g, bridge, ImageKey(index), out JArray path))
        {
            return false;
        }
        controlImage = new WGNodeData(path, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
        return true;
    }

    /// <summary>Returns the original full video batch behind core's single-frame wrapper and
    /// attaches the soundtrack captured from the same host ControlNet input.</summary>
    internal bool TryGetCapturedControlVideo(int index, out WGNodeData controlVideo)
    {
        controlVideo = null;
        if (!TryGetCapturedControlImage(index, out WGNodeData controlImage)
            || controlImage.Path is not JArray { Count: 2 } imagePath)
        {
            return false;
        }
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        JArray fullVideoPath = PeelSingleFrameWrap(bridge, imagePath);
        controlVideo = new WGNodeData(
            fullVideoPath,
            g,
            WGNodeData.DT_IMAGE,
            g.CurrentCompat());
        if (TryGetCapturedAudio(index, out WGNodeData audio))
        {
            controlVideo.AttachedAudio = audio;
        }
        return true;
    }

    internal bool TryGetCapturedAudio(int index, out WGNodeData audio)
    {
        audio = null;
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (!IsValidIndex(index)
            || !VideoGraphHelpers.TryGetCachedPath(
                g, bridge, AudioKey(index), out JArray path)
            || bridge.ResolvePath(path) is not INodeOutput output)
        {
            return false;
        }
        audio = output.ToWGNodeData(
            g,
            WGNodeData.DT_AUDIO,
            g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
        return true;
    }

    internal bool TryGetCapturedApplyImageInput(
        WorkflowBridge bridge,
        int index,
        out JArray controlImage)
    {
        controlImage = null;
        return IsValidIndex(index)
            && g.NodeHelpers.TryGetValue(ApplyKey(index), out string applyId)
            && bridge.Graph.GetNode(applyId) is not null
            && g.Workflow[applyId] is JObject applyNode
            && VideoGraphHelpers.TryGetInputRef(applyNode, "image", out controlImage);
    }

    private void CaptureUpstreamAudio(
        WorkflowBridge bridge,
        JArray controlImage,
        int index)
    {
        if (!IsValidIndex(index))
        {
            return;
        }
        ComfyNode startNode = bridge.NodeAt(controlImage);
        GetVideoComponentsNode components = startNode as GetVideoComponentsNode
            ?? bridge.Graph.FindNearestUpstream<GetVideoComponentsNode>(startNode);
        if (components is null)
        {
            VideoGraphHelpers.RemoveCached(g, AudioKey(index));
            return;
        }
        VideoGraphHelpers.CachePath(
            g,
            AudioKey(index),
            WorkflowBridge.ToPath(components.Audio));
    }

    internal static bool HasVideoUpstream(WorkflowBridge bridge, JArray outputRef)
    {
        if (outputRef is not { Count: 2 } || bridge.NodeAt(outputRef) is not ComfyNode start)
        {
            return false;
        }
        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [start.Id];
        pending.Enqueue(start);
        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            if (current is SwarmLoadVideoB64Node or GetVideoComponentsNode)
            {
                return true;
            }
            // FindUpstream, not Inputs: it also reads autogrow list children, so a batch node
            // between here and the loaded footage does not hide it.
            foreach (ComfyNode upstream in bridge.Graph.FindUpstream(current))
            {
                if (visited.Add(upstream.Id))
                {
                    pending.Enqueue(upstream);
                }
            }
        }
        return false;
    }

    internal static JArray PeelSingleFrameWrap(
        WorkflowBridge bridge,
        JArray imagePath)
    {
        if (bridge.NodeAt<ImageFromBatchNode>(imagePath) is ImageFromBatchNode batch
            && batch.BatchIndex.LiteralAsInt() == 0
            && batch.Length.LiteralAsInt() == 1
            && batch.Image.Connection is INodeOutput imageIn)
        {
            return WorkflowBridge.ToPath(imageIn);
        }
        return new JArray(imagePath[0], imagePath[1]);
    }

    private bool TryFindCoreApply(
        WorkflowBridge bridge,
        T2IModel controlModel,
        ISet<string> usedApplyNodes,
        out string applyId,
        out JArray fullControlImage)
    {
        applyId = null;
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
                    || !VideoGraphHelpers.TryGetInputRef(
                        candidateNode, loaderInputName, out JArray loaderRef)
                    || !VideoGraphHelpers.TryGetInputRef(
                        candidateNode, "image", out JArray imageInput)
                    || !LoaderChainContainsModel(bridge, loaderRef, controlModelName)
                    || !HasVideoUpstream(bridge, imageInput))
                {
                    continue;
                }
                applyId = candidate.Id;
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
            || LoaderMatches(
                bridge.Graph.FindNearestUpstream<ControlNetLoaderNode>(start),
                controlModelName)
            || LoaderMatches(
                bridge.Graph.FindNearestUpstream<ModelPatchLoaderNode>(start),
                controlModelName);
    }

    private static bool LoaderMatches(ComfyNode node, string controlModelName) => node switch
    {
        ControlNetLoaderNode cn => cn.ControlNetName.LiteralAsString() == controlModelName,
        ModelPatchLoaderNode mp => mp.Name.LiteralAsString() == controlModelName,
        _ => false,
    };

    private void Clear(int index)
    {
        VideoGraphHelpers.RemoveCached(g, ImageKey(index));
        VideoGraphHelpers.RemoveCached(g, AudioKey(index));
        VideoGraphHelpers.RemoveCached(g, ApplyKey(index));
    }

    internal static bool IsValidIndex(int index) =>
        index >= 0 && index < T2IParamTypes.Controlnets.Length;

    private static string ImageKey(int index) => $"{ImagePrefix}{index}";

    private static string AudioKey(int index) => $"{AudioPrefix}{index}";

    private static string ApplyKey(int index) => $"{ApplyPrefix}{index}";
}
