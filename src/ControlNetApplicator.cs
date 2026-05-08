using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;

namespace VideoStages;

internal static class ControlNetApplicator
{
    private const string CapturedControlNetImageKeyPrefix = "videostages.controlnet.fullimage.";
    private const string CapturedControlNetFrameCountKeyPrefix = "videostages.controlnet.framecount.";

    private static string CapturedControlNetImageKey(int index) =>
        $"{CapturedControlNetImageKeyPrefix}{index}";

    private static string CapturedControlNetFrameCountKey(int index) =>
        $"{CapturedControlNetFrameCountKeyPrefix}{index}";

    public static void CaptureCoreVideoControlNetPreprocessors(WorkflowGenerator g)
    {
        HashSet<string> nodes = [];
        for (int i = 0; i < T2IParamTypes.Controlnets.Length; i++)
        {
            T2IParamTypes.ControlNetParamHolder controlnetParams = T2IParamTypes.Controlnets[i];
            if (controlnetParams is null
                || !g.UserInput.TryGet(controlnetParams.Strength, out double _)
                || !g.UserInput.TryGet(controlnetParams.Model, out T2IModel model)
                || !TryFindCoreControlNetApply(g, model, nodes, out (string Id, JObject Node) applyNode, out JArray controlImage)
                || !OutputHasVideoUpstream(g.Workflow, controlImage))
            {
                g.NodeHelpers.Remove(CapturedControlNetImageKey(i));
                continue;
            }

            EnsureResizeMultiple(g, controlImage);
            JArray capturePath = new(controlImage[0], controlImage[1]);
            g.NodeHelpers[CapturedControlNetImageKey(i)] = capturePath.ToString(Formatting.None);
            EnsureSingleFrameWrap(g, controlImage);
            nodes.Add(applyNode.Id);
        }
    }

    private static void EnsureResizeMultiple(WorkflowGenerator g, JArray controlImage)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
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
        // If core wrapped the apply image in ImageFromBatch(length=1), the resize must go
        // upstream of the wrap — otherwise GetImageSize on the resize sees batch_size=1 and the
        // IC-LoRA guide receives a single frame instead of the full video.
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
            BridgeSync.SyncLastId(g);
            return;
        }
        ResizeImageMaskNodeNode resize = bridge.AddNode(new ResizeImageMaskNodeNode()).With(
            ResizeType: "scale to multiple",
            ScaleMethod: "lanczos");
        resize.Input.ConnectToUntyped(consumerOutput);
        resize.ExtraInputs["resize_type.multiple"] = 64;
        bridge.SyncNode(resize);
        BridgeSync.SyncLastId(g);
        controlImage[0] = resize.Id;
        controlImage[1] = 0;
    }

    private static void EnsureSingleFrameWrap(WorkflowGenerator g, JArray controlImage)
    {
        if (OutputRefIsNodeType<ImageFromBatchNode>(g.Workflow, controlImage))
        {
            return;
        }
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ImageFromBatchNode batch = bridge.AddNode(new ImageFromBatchNode()).With(
            BatchIndex: 0,
            Length: 1);
        batch.Image.TryConnectToUntyped(bridge.ResolvePath(controlImage));
        bridge.SyncNode(batch);
        BridgeSync.SyncLastId(g);
        controlImage[0] = batch.Id;
        controlImage[1] = 0;
    }

    private static ResizeImageMaskNodeNode FindUpstreamScaleToMultipleResize(
        WorkflowBridge bridge,
        JArray startRef)
    {
        INodeOutput current = bridge.ResolvePath(startRef);
        for (int i = 0; i < 4 && current is not null; i++)
        {
            if (current.Node is ResizeImageMaskNodeNode candidate
                && candidate.ResizeType.LiteralAsString() == "scale to multiple")
            {
                return candidate;
            }
            INodeInput next = current.Node.FindInput("image") ?? current.Node.FindInput("input");
            current = next?.Connection;
        }
        return null;
    }

    public static bool Apply(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        string source,
        double? stageControlNetStrength,
        int? frameCount,
        bool clipLengthFromControlNet = false)
    {
        if (genInfo.VideoModel.ModelClass.CompatClass.ID != T2IModelClassSorter.CompatLtxv2.ID)
        {
            return false;
        }

        int index = ParseControlNetSourceIndex(source);
        if (!TryGetCapturedCoreControlImage(g, index, out WGNodeData controlImage))
        {
            return false;
        }

        if (!OutputRefIsNodeType<LTXICLoRALoaderModelOnlyNode>(g.Workflow, genInfo.Model.Path))
        {
            Logs.Warning(
                $"VideoStages: ControlNet '{source}' is configured but no controlnet_lora "
                + "is set for this stage; skipping ControlNet.");
            return false;
        }

        T2IParamTypes.ControlNetParamHolder controlnetParams = T2IParamTypes.Controlnets[index];
        JToken guideFrameCount = ResolveGuideFrameCount(g, genInfo, frameCount, source, clipLengthFromControlNet);
        double strength = stageControlNetStrength ?? g.UserInput.Get(controlnetParams.Strength);

        return ApplyLtxIcloraGuide(g, genInfo, controlImage, strength, guideFrameCount);
    }

    public static bool TryCreateCapturedControlImageFrameCount(
        WorkflowGenerator g,
        string controlNetSource,
        out JArray framesConnection)
    {
        framesConnection = null;
        int index = ParseControlNetSourceIndex(controlNetSource);
        string helperKey = CapturedControlNetFrameCountKey(index);
        if (g.NodeHelpers.TryGetValue(helperKey, out string encoded)
            && TryParseConnection(encoded, out framesConnection))
        {
            return true;
        }

        if (!TryGetCapturedCoreControlImage(g, index, out WGNodeData controlImage)
            || controlImage.Path is not JArray { Count: 2 } controlImagePath)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ResizeImageMaskNodeNode upstreamResize =
            FindUpstreamScaleToMultipleResize(bridge, controlImagePath);
        INodeOutput frameSource = upstreamResize?.Resized
            ?? bridge.ResolvePath(ResolveFullControlImageSource(g.Workflow, controlImagePath));
        if (frameSource is null
            || !OutputHasVideoUpstream(g.Workflow, WorkflowBridge.ToPath(frameSource)))
        {
            return false;
        }

        GetImageSizeNode sizeNode = bridge.AddNode(new GetImageSizeNode());
        sizeNode.Image.ConnectToUntyped(frameSource);
        bridge.SyncNode(sizeNode);
        BridgeSync.SyncLastId(g);
        framesConnection = WorkflowBridge.ToPath(sizeNode.BatchSize);
        g.NodeHelpers[helperKey] = framesConnection.ToString(Formatting.None);
        return true;
    }

    private static JToken ResolveGuideFrameCount(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int? stageClipFrames,
        string controlNetSource,
        bool clipLengthFromControlNet)
    {
        if (clipLengthFromControlNet
            && TryCreateCapturedControlImageFrameCount(g, controlNetSource, out JArray framesConnection))
        {
            return framesConnection;
        }
        int? frames = stageClipFrames ?? genInfo?.Frames;
        return frames is int n ? new JValue(n) : null;
    }

    private static bool ApplyLtxIcloraGuide(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData controlImage,
        double strength,
        JToken frameCount)
    {
        if (strength <= 0)
        {
            return false;
        }
        if (!g.Features.Contains(Constants.LtxVideoFeatureFlag))
        {
            throw new SwarmUserErrorException(
                "VideoStages ControlNet IC-LoRA guides require the ComfyUI-LTXVideo custom nodes. "
                + $"Install {Constants.LtxVideoNodeUrl} or use SwarmUI's LTXVideo feature installer.");
        }

        JArray guideImagePath = ControlImageForLtxIcloraGuide(g, controlImage.Path, frameCount);

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LTXAddVideoICLoRAGuideNode guide = bridge.AddNode(new LTXAddVideoICLoRAGuideNode().With(
            FrameIdx: 0,
            Strength: strength,
            LatentDownscaleFactor: 2.0,
            Crop: "disabled",
            UseTiledEncode: false,
            TileSize: 256,
            TileOverlap: 64));
        guide.PositiveInput.ConnectToUntyped(bridge.ResolvePath(genInfo.PosCond));
        guide.NegativeInput.ConnectToUntyped(bridge.ResolvePath(genInfo.NegCond));
        guide.Vae.ConnectToUntyped(bridge.ResolvePath(genInfo.Vae.Path));
        guide.LatentInput.ConnectToUntyped(bridge.ResolvePath(g.CurrentMedia.Path));
        guide.Image.ConnectToUntyped(bridge.ResolvePath(guideImagePath));
        bridge.SyncNode(guide);
        BridgeSync.SyncLastId(g);

        genInfo.PosCond = new JArray(guide.Id, 0);
        genInfo.NegCond = new JArray(guide.Id, 1);
        g.CurrentMedia = g.CurrentMedia.WithPath(
            new JArray(guide.Id, 2),
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat);
        return true;
    }

    private static JArray ControlImageForLtxIcloraGuide(
        WorkflowGenerator g,
        JArray controlImagePath,
        JToken frames)
    {
        if (frames is null)
        {
            return new JArray(controlImagePath[0], controlImagePath[1]);
        }

        JArray guideSource = ResolveFullControlImageSource(g.Workflow, controlImagePath);
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        string croppedGuide = AddImageFromBatch(
            bridge,
            guideSource,
            batchIndex: 0,
            lengthToken: frames.DeepClone());
        BridgeSync.SyncLastId(g);
        return new JArray(croppedGuide, 0);
    }

    private static string AddImageFromBatch(
        WorkflowBridge bridge,
        JArray imagePath,
        int batchIndex,
        JToken lengthToken)
    {
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode()).With(
            BatchIndex: batchIndex);
        node.Image.TryConnectToUntyped(bridge.ResolvePath(imagePath));
        if (lengthToken is JArray lengthRef)
        {
            node.Length.TryConnectToUntyped(bridge.ResolvePath(lengthRef));
        }
        else if (lengthToken is JValue v && v.Value is not null)
        {
            node.Length.Set(Convert.ToInt64(v.Value));
        }
        bridge.SyncNode(node);
        return node.Id;
    }

    private static JArray ResolveFullControlImageSource(
        JObject workflow,
        JArray capturedApplyImageRef)
    {
        if (capturedApplyImageRef is not { Count: 2 })
        {
            return capturedApplyImageRef;
        }

        if (!TryGetSingleImageFromBatchInput(
                workflow,
                capturedApplyImageRef,
                out JArray batchInput))
        {
            return new JArray(capturedApplyImageRef[0], capturedApplyImageRef[1]);
        }

        return batchInput;
    }

    private static bool TryGetSingleImageFromBatchInput(
        JObject workflow,
        JArray imageRef,
        out JArray inputRef)
    {
        inputRef = null;
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        if (bridge.Graph.GetNode<ImageFromBatchNode>($"{imageRef[0]}") is not ImageFromBatchNode batch
            || batch.Length.LiteralAsInt() != 1
            || batch.Image.Connection is not INodeOutput imageIn)
        {
            return false;
        }

        inputRef = new JArray(imageIn.Node.Id, imageIn.SlotIndex);
        return true;
    }

    private static bool TryFindCoreControlNetApply(
        WorkflowGenerator g,
        T2IModel controlModel,
        ISet<string> processedApplyNodes,
        out (string Id, JObject Node) applyNode,
        out JArray fullControlImage)
    {
        applyNode = default;
        fullControlImage = null;
        string controlModelName = controlModel.ToString(g.ModelFolderFormat);
        foreach ((string Id, JObject Node) candidate in CoreControlNetApplyNodes(g.Workflow))
        {
            if (processedApplyNodes.Contains(candidate.Id)
                || !ControlApplyUsesModel(g.Workflow, candidate.Node, controlModelName)
                || !TryGetInputRef(candidate.Node, "image", out JArray imageInput))
            {
                continue;
            }

            applyNode = candidate;
            fullControlImage = imageInput;
            return true;
        }
        return false;
    }

    private static IEnumerable<(string Id, JObject Node)> CoreControlNetApplyNodes(JObject workflow)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        return bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>()
            .Select(n => (n.Id, (JObject)workflow[n.Id]))
            .OrderBy(t => NodeSortValue(t.Id));
    }

    private static int NodeSortValue(string nodeId)
    {
        return int.TryParse(nodeId, out int id) ? id : int.MaxValue;
    }

    private static bool ControlApplyUsesModel(
        JObject workflow,
        JObject applyNode,
        string controlModelName)
    {
        if (!TryGetInputRef(applyNode, "control_net", out JArray controlNetRef))
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        return InputChainUsesLoader<ControlNetLoaderNode>(
            bridge,
            controlNetRef,
            node => node.ControlNetName.LiteralAsString(),
            controlModelName);
    }

    private static bool InputChainUsesLoader<TLoader>(
        WorkflowBridge bridge,
        JArray inputRef,
        Func<TLoader, string> readModelName,
        string modelName) where TLoader : ComfyNode
    {
        if (inputRef is not { Count: 2 })
        {
            return false;
        }

        Queue<string> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue($"{inputRef[0]}");
        while (pending.Count > 0)
        {
            string nodeId = pending.Dequeue();
            if (string.IsNullOrWhiteSpace(nodeId) || !visited.Add(nodeId))
            {
                continue;
            }
            ComfyNode node = bridge.Graph.GetNode(nodeId);
            if (node is null)
            {
                continue;
            }
            if (node is TLoader loader && readModelName(loader) == modelName)
            {
                return true;
            }
            foreach (INodeInput input in node.Inputs)
            {
                if (input.Connection?.Node is ComfyNode upstream)
                {
                    pending.Enqueue(upstream.Id);
                }
            }
        }
        return false;
    }

    private static bool OutputHasVideoUpstream(JObject workflow, JArray outputRef)
    {
        if (outputRef is not { Count: 2 })
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode startNode = bridge.Graph.GetNode($"{outputRef[0]}");
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

    private static bool OutputRefIsNodeType<TNode>(JObject workflow, JArray outputRef) where TNode : ComfyNode
    {
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        return bridge.Graph.GetNode($"{outputRef[0]}") is TNode;
    }

    private static bool TryGetCapturedCoreControlImage(WorkflowGenerator g, int index, out WGNodeData controlImage)
    {
        controlImage = null;
        if (!g.NodeHelpers.TryGetValue(CapturedControlNetImageKey(index), out string encoded)
            || string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            if (JToken.Parse(encoded) is not JArray { Count: 2 } path)
            {
                return false;
            }
            controlImage = new WGNodeData(
                new JArray(path[0], path[1]),
                g,
                WGNodeData.DT_IMAGE,
                g.CurrentCompat());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseConnection(string encoded, out JArray connection)
    {
        connection = null;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }
        try
        {
            if (JToken.Parse(encoded) is not JArray { Count: 2 } path)
            {
                return false;
            }
            connection = new JArray(path[0], path[1]);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetInputRef(JObject node, string inputName, out JArray inputRef)
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

    public static int ParseControlNetSourceIndex(string controlNetSource)
    {
        string compact = StringUtils.Compact(controlNetSource);
        if (StringUtils.Equals(compact, "ControlNet1"))
        {
            return 0;
        }
        if (StringUtils.Equals(compact, "ControlNet2"))
        {
            return 1;
        }
        if (StringUtils.Equals(compact, "ControlNet3"))
        {
            return 2;
        }
        return 0;
    }

}
