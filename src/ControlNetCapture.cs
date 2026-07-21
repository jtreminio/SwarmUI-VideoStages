using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal class ControlNetCapture(WorkflowGenerator g)
{
    private const string CapturedControlNetImageKeyPrefix = "videostages.controlnet.fullimage.";
    private const string CapturedControlNetFrameCountKeyPrefix = "videostages.controlnet.framecount.";
    private const string CapturedControlNetAudioKeyPrefix = "videostages.controlnet.audio.";

    private static readonly (string ApplyClass, string LoaderInputName)[] KnownControlNetApplyNodes =
    [
        (ControlNetApplyAdvancedNode.ClassType, "control_net"),
        (ControlNetInpaintingAliMamaApplyNode.ClassType, "control_net"),
        (QwenImageDiffsynthControlnetNode.ClassType, "model_patch"),
    ];

    private static string CapturedControlNetImageKey(int index) =>
        $"{CapturedControlNetImageKeyPrefix}{index}";

    private static string CapturedControlNetFrameCountKey(int index) =>
        $"{CapturedControlNetFrameCountKeyPrefix}{index}";

    private static string CapturedControlNetAudioKey(int index) =>
        $"{CapturedControlNetAudioKeyPrefix}{index}";

    public void CaptureCoreVideoControlNetPreprocessors()
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        HashSet<string> usedApplyNodes = [];
        for (int i = 0; i < T2IParamTypes.Controlnets.Length; i++)
        {
            T2IParamTypes.ControlNetParamHolder controlnetParams = T2IParamTypes.Controlnets[i];
            if (controlnetParams is null
                || !g.UserInput.TryGet(controlnetParams.Strength, out double _)
                || !g.UserInput.TryGet(controlnetParams.Model, out T2IModel model)
                || !TryFindCoreControlNetApply(bridge, model, usedApplyNodes, out (string Id, JObject Node) applyNode, out JArray controlImage)
                || !OutputHasVideoUpstream(bridge, controlImage))
            {
                g.NodeHelpers.Remove(CapturedControlNetImageKey(i));
                g.NodeHelpers.Remove(CapturedControlNetAudioKey(i));
                continue;
            }

            EnsureResizeMultiple(bridge, controlImage);
            JArray capturePath = new(controlImage[0], controlImage[1]);
            g.NodeHelpers[CapturedControlNetImageKey(i)] = capturePath.ToString(Formatting.None);
            CaptureUpstreamGetVideoComponentsAudio(bridge, controlImage, i);
            EnsureSingleFrameWrap(bridge, controlImage);
            usedApplyNodes.Add(applyNode.Id);
        }
    }

    private void CaptureUpstreamGetVideoComponentsAudio(
        WorkflowBridge bridge,
        JArray controlImage,
        int index)
    {
        ComfyNode startNode = bridge.NodeAt(controlImage);
        GetVideoComponentsNode components = startNode as GetVideoComponentsNode
            ?? bridge.Graph.FindNearestUpstream<GetVideoComponentsNode>(startNode);
        if (components is not null)
        {
            JArray audioPath = WorkflowBridge.ToPath(components.Audio);
            g.NodeHelpers[CapturedControlNetAudioKey(index)] = audioPath.ToString(Formatting.None);
            return;
        }
        g.NodeHelpers.Remove(CapturedControlNetAudioKey(index));
    }

    public bool TryGetCapturedControlNetAudio(string controlNetSource, out WGNodeData audio)
    {
        audio = null;
        if (!TryParseControlNetSourceIndex(controlNetSource, out int index))
        {
            return false;
        }
        if (!g.NodeHelpers.TryGetValue(CapturedControlNetAudioKey(index), out string encoded)
            || string.IsNullOrWhiteSpace(encoded)
            || JToken.Parse(encoded) is not JArray { Count: 2 } path)
        {
            return false;
        }
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput output = bridge.ResolvePath(path);
        if (output is null)
        {
            return false;
        }
        audio = output.ToWGNodeData(g, WGNodeData.DT_AUDIO, g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
        return true;
    }

    private void EnsureResizeMultiple(WorkflowBridge bridge, JArray controlImage)
    {
        if (FindUpstreamScaleToMultipleResize(bridge, controlImage)
            is ResizeImageMaskNodeNode existing)
        {
            existing.ExtraInputs["resize_type.multiple"] = 64;
            bridge.SyncNode(existing);
            return;
        }
        if (bridge.ResolvePath(controlImage) is not INodeOutput consumerOutput)
        {
            return;
        }
        if (consumerOutput.Node is ImageFromBatchNode batch
            && batch.Length.LiteralAsInt() == 1
            && batch.Image.Connection is INodeOutput batchSource)
        {
            ResizeImageMaskNodeNode rewired = bridge.AddNode(new ResizeImageMaskNodeNode()).With(
                ResizeType: "scale to multiple",
                ScaleMethod: "lanczos");
            rewired.Input.ConnectToUntyped(batchSource);
            rewired.ExtraInputs["resize_type.multiple"] = 64;
            batch.Image.ConnectToUntyped(rewired.Resized);
            bridge.SyncNode(rewired);
            bridge.SyncNode(batch);
            return;
        }
        ResizeImageMaskNodeNode resize = bridge.AddNode(new ResizeImageMaskNodeNode()).With(
            ResizeType: "scale to multiple",
            ScaleMethod: "lanczos");
        resize.Input.ConnectToUntyped(consumerOutput);
        resize.ExtraInputs["resize_type.multiple"] = 64;
        bridge.SyncNode(resize);
        controlImage[0] = resize.Id;
        controlImage[1] = 0;
    }

    private void EnsureSingleFrameWrap(WorkflowBridge bridge, JArray controlImage)
    {
        if (bridge.NodeAt(controlImage) is ImageFromBatchNode)
        {
            return;
        }
        ImageFromBatchNode batch = bridge.AddNode(new ImageFromBatchNode()).With(
            BatchIndex: 0,
            Length: 1);
        batch.Image.TryConnectFromPath(bridge, controlImage);
        bridge.SyncNode(batch);
        controlImage[0] = batch.Id;
        controlImage[1] = 0;
    }

    private static ResizeImageMaskNodeNode FindUpstreamScaleToMultipleResize(
        WorkflowBridge bridge,
        JArray startRef)
    {
        if (bridge.ResolvePath(startRef)?.Node is not ComfyNode start)
        {
            return null;
        }
        return (IsScaleToMultipleResize(start)
            ? start
            : bridge.Graph.FindNearestUpstream(start, IsScaleToMultipleResize))
            as ResizeImageMaskNodeNode;
    }

    private static bool IsScaleToMultipleResize(ComfyNode node) =>
        node is ResizeImageMaskNodeNode resize
        && resize.ResizeType.LiteralAsString() == "scale to multiple";

    public bool TryCreateCapturedControlImageFrameCount(
        string controlNetSource,
        out JArray framesConnection)
    {
        framesConnection = null;
        if (string.IsNullOrWhiteSpace(controlNetSource))
        {
            return false;
        }
        int index = ParseControlNetSourceIndex(controlNetSource);
        string helperKey = CapturedControlNetFrameCountKey(index);
        if (VideoGraphHelpers.TryGetCachedPath(g, bridge: null, helperKey, out JArray cached))
        {
            framesConnection = cached;
            return true;
        }

        if (!TryGetCapturedCoreControlImage(index, out WGNodeData controlImage)
            || controlImage.Path is not JArray { Count: 2 } controlImagePath)
        {
            return false;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        ResizeImageMaskNodeNode upstreamResize =
            FindUpstreamScaleToMultipleResize(bridge, controlImagePath);
        INodeOutput frameSource = upstreamResize?.Resized
            ?? bridge.ResolvePath(PeelSingleFrameWrap(bridge, controlImagePath));
        if (frameSource is null
            || !OutputHasVideoUpstream(bridge, WorkflowBridge.ToPath(frameSource)))
        {
            return false;
        }

        GetImageSizeNode sizeNode = bridge.AddNode(new GetImageSizeNode());
        sizeNode.Image.ConnectToUntyped(frameSource);
        bridge.SyncNode(sizeNode);
        framesConnection = WorkflowBridge.ToPath(sizeNode.BatchSize);
        g.NodeHelpers[helperKey] = framesConnection.ToString(Formatting.None);
        return true;
    }

    internal static JArray PeelSingleFrameWrap(WorkflowBridge bridge, JArray imagePath)
    {
        if (bridge.NodeAt<ImageFromBatchNode>(imagePath) is ImageFromBatchNode batch
            && batch.Length.LiteralAsInt() == 1
            && batch.Image.Connection is INodeOutput imageIn)
        {
            return WorkflowBridge.ToPath(imageIn);
        }
        return new JArray(imagePath[0], imagePath[1]);
    }

    private bool TryFindCoreControlNetApply(
        WorkflowBridge bridge,
        T2IModel controlModel,
        ISet<string> usedApplyNodes,
        out (string Id, JObject Node) applyNode,
        out JArray fullControlImage)
    {
        applyNode = default;
        fullControlImage = null;
        string controlModelName = controlModel.ToString(g.ModelFolderFormat);

        foreach ((string applyClass, string loaderInputName) in KnownControlNetApplyNodes)
        {
            IEnumerable<ComfyNode> candidates = bridge.Graph.NodesOfType(applyClass)
                .OrderBy(n => int.TryParse(n.Id, out int id) ? id : int.MaxValue);

            foreach (ComfyNode candidate in candidates)
            {
                if (usedApplyNodes.Contains(candidate.Id))
                {
                    continue;
                }
                if (g.Workflow[candidate.Id] is not JObject candidateNode
                    || !VideoGraphHelpers.TryGetInputRef(candidateNode, loaderInputName, out JArray loaderRef)
                    || !VideoGraphHelpers.TryGetInputRef(candidateNode, "image", out JArray imageInput))
                {
                    continue;
                }
                if (!LoaderChainContainsModel(bridge, loaderRef, controlModelName))
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

    private static bool OutputHasVideoUpstream(WorkflowBridge bridge, JArray outputRef)
    {
        if (outputRef is not { Count: 2 })
        {
            return false;
        }

        ComfyNode startNode = bridge.NodeAt(outputRef);
        if (startNode is null)
        {
            return false;
        }

        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(startNode);
        visited.Add(startNode.Id);
        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            if (current is SwarmLoadVideoB64Node or GetVideoComponentsNode)
            {
                return true;
            }
            foreach (INodeInput input in current.Inputs)
            {
                if (input.Connection?.Node is ComfyNode upstream && visited.Add(upstream.Id))
                {
                    pending.Enqueue(upstream);
                }
            }
        }
        return false;
    }

    internal bool TryGetCapturedCoreControlImage(int index, out WGNodeData controlImage)
    {
        controlImage = null;
        if (!g.NodeHelpers.TryGetValue(CapturedControlNetImageKey(index), out string encoded)
            || string.IsNullOrWhiteSpace(encoded)
            || JToken.Parse(encoded) is not JArray { Count: 2 } path)
        {
            return false;
        }
        controlImage = new WGNodeData(
            path,
            g,
            WGNodeData.DT_IMAGE,
            g.CurrentCompat());
        return true;
    }

    private static int ParseControlNetSourceIndex(string controlNetSource)
    {
        if (TryParseControlNetSourceIndex(controlNetSource, out int index))
        {
            return index;
        }
        throw new SwarmUserErrorException($"Unrecognized ControlNet source: '{controlNetSource}'");
    }

    internal static bool TryParseControlNetSourceIndex(string controlNetSource, out int index)
    {
        string compact = StringUtils.Compact(controlNetSource);
        if (StringUtils.Equals(compact, "ControlNet1"))
        {
            index = 0;
            return true;
        }
        if (StringUtils.Equals(compact, "ControlNet2"))
        {
            index = 1;
            return true;
        }
        if (StringUtils.Equals(compact, "ControlNet3"))
        {
            index = 2;
            return true;
        }
        index = -1;
        return false;
    }
}
