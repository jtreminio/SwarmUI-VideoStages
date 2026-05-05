using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using ComfyTyped.Types;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using VideoStages.LTX2;
using VideoStages.Typed;

namespace VideoStages;

internal static class ControlNetApplicator
{
    private const string CapturedControlNetImageKeyPrefix = "videostages.controlnet.fullimage.";
    private const string CapturedControlNetFrameCountKeyPrefix = "videostages.controlnet.framecount.";
    private const string NeedsLtxIcloraGuideCropKey = "videostages.controlnet.ltx_ic_lora_guide.needs_crop";

    public static void CaptureCoreVideoControlNetPreprocessors(WorkflowGenerator g)
    {
        if (!HasConfiguredVideoStages(g))
        {
            return;
        }

        HashSet<string> processedApplyNodes = [];
        for (int i = 0; i < T2IParamTypes.Controlnets.Length; i++)
        {
            T2IParamTypes.ControlNetParamHolder controlnetParams = T2IParamTypes.Controlnets[i];
            if (controlnetParams is null
                || !g.UserInput.TryGet(controlnetParams.Strength, out double _))
            {
                g.NodeHelpers.Remove(CapturedControlNetImageKey(i));
                continue;
            }

            T2IModel controlModel = g.UserInput.Get(controlnetParams.Model, null);
            if (controlModel is null)
            {
                g.NodeHelpers.Remove(CapturedControlNetImageKey(i));
                continue;
            }

            if (!TryFindCoreControlNetApply(
                    g,
                    controlModel,
                    processedApplyNodes,
                    out (string Id, JObject Node) applyNode,
                    out JArray fullControlImage)
                || !OutputHasVideoUpstream(g.Workflow, fullControlImage))
            {
                g.NodeHelpers.Remove(CapturedControlNetImageKey(i));
                continue;
            }

            ReplaceVideoControlNetUpscale(g, fullControlImage);
            JArray capturePath = new(fullControlImage[0], fullControlImage[1]);
            g.NodeHelpers[CapturedControlNetImageKey(i)] = capturePath.ToString(Formatting.None);
            if (OutputRefIsNodeType<ImageFromBatchNode>(g.Workflow, fullControlImage))
            {
                processedApplyNodes.Add(applyNode.Id);
                continue;
            }

            string firstFrameNode = AddImageFromBatch(g, fullControlImage, batchIndex: 0, lengthLiteral: 1L);
            fullControlImage[0] = firstFrameNode;
            fullControlImage[1] = 0;

            processedApplyNodes.Add(applyNode.Id);
        }
    }

    /// <summary>
    /// Walks upstream from <paramref name="fullControlImage"/> looking for an
    /// <c>ImageScale</c> whose source is a <c>GetVideoComponents</c>; when found, transforms
    /// it in place into a <c>ResizeImageMaskNode</c>.
    ///
    /// <para>Detection (typed graph walk + pattern match) and mutation (JObject rewrite of
    /// <c>class_type</c> and inputs) live side-by-side because the swap preserves the node's
    /// id. Round-tripping the class change through <c>bridge.RemoveNode</c> +
    /// <c>bridge.AddNode</c> would orphan every consumer's typed connection — messier than
    /// just rewriting the JObject in place.</para>
    /// </summary>
    private static void ReplaceVideoControlNetUpscale(WorkflowGenerator g, JArray fullControlImage)
    {
        if (fullControlImage is not { Count: 2 })
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.ResolvePath(fullControlImage) is not INodeOutput startOutput)
        {
            return;
        }

        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(startOutput.Node);
        visited.Add(startOutput.Node.Id);
        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            if (current is ImageScaleNode scale
                && scale.Image.Connection is INodeOutput sourceImage
                && sourceImage.Node is GetVideoComponentsNode)
            {
                JObject node = (JObject)bridge.Workflow[scale.Id];
                JObject inputs = (JObject)node["inputs"];
                node["class_type"] = NodeTypes.ResizeImageMaskNode;
                inputs.Remove("image");
                inputs.Remove("width");
                inputs.Remove("height");
                inputs.Remove("upscale_method");
                inputs.Remove("crop");
                inputs["input"] = new JArray(sourceImage.Node.Id, sourceImage.SlotIndex);
                inputs["resize_type"] = "scale shorter dimension";
                inputs["resize_type.shorter_size"] = 512;
                inputs["scale_method"] = "lanczos";
                return;
            }

            foreach (INodeInput input in current.Inputs)
            {
                if (input.Connection?.Node is ComfyNode upstream && visited.Add(upstream.Id))
                {
                    pending.Enqueue(upstream);
                }
            }
        }
    }

    public static void Apply(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia,
        string controlNetSource,
        double? stageControlNetStrength,
        bool clipLengthFromControlNet = false)
    {
        int index = ParseControlNetSourceIndex(controlNetSource);
        if (sourceMedia is null || genInfo is null || genInfo.PosCond is null || genInfo.NegCond is null)
        {
            return;
        }

        if (!VideoStageModelCompat.IsLtxV2VideoModel(genInfo.VideoModel))
        {
            return;
        }

        T2IParamTypes.ControlNetParamHolder controlnetParams = T2IParamTypes.Controlnets[index];
        if (controlnetParams is null
            || !g.UserInput.TryGet(controlnetParams.Strength, out double controlStrength))
        {
            return;
        }

        T2IModel controlModel = g.UserInput.Get(controlnetParams.Model, null);
        if (!TryGetCapturedCoreControlImage(g, index, out WGNodeData controlImage))
        {
            controlImage = sourceMedia.AsRawImage(genInfo.Vae ?? g.CurrentVae);
            string preprocessor = ResolvePreprocessor(g, index, controlModel);
            if (!StringUtils.Equals(preprocessor, "none"))
            {
                WGNodeData priorVae = g.CurrentVae;
                try
                {
                    g.CurrentVae = genInfo.Vae ?? priorVae;
                    JArray preprocessed = g.CreatePreprocessor(preprocessor, controlImage);
                    g.NodeHelpers["controlnet_preprocessor"] = $"{preprocessed[0]}";
                    controlImage = controlImage.WithPath(preprocessed);
                }
                finally
                {
                    g.CurrentVae = priorVae;
                }
            }
        }

        if (controlModel is null)
        {
            throw new SwarmUserErrorException(
                "Cannot use VideoStages ControlNet source without a ControlNet model selected.");
        }

        JToken guideFrameCount = ResolveGuideFrameCount(
            g,
            genInfo,
            controlNetSource,
            clipLengthFromControlNet);
        if (TryApplyLtxIcloraGuide(g, genInfo, controlImage, stageControlNetStrength ?? controlStrength, guideFrameCount))
        {
            return;
        }

        if (controlModel.ModelClass?.ID?.EndsWith("/control-diffpatch") ?? false)
        {
            ApplyDiffPatchControlNet(g, genInfo, controlImage, controlModel, controlStrength);
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ControlNetLoaderNode loader = bridge.AddNode(new ControlNetLoaderNode());
        loader.ControlNetName.Set(controlModel.ToString(g.ModelFolderFormat));
        bridge.SyncNode(loader);

        NodeOutput<ControlNetType> controlNetOutput = loader.CONTROLNET;
        if (TryGetUnionType(g, index, out string unionType))
        {
            SetUnionControlNetTypeNode union = bridge.AddNode(new SetUnionControlNetTypeNode());
            union.ControlNet.ConnectTo(controlNetOutput);
            union.Type.Set(unionType);
            bridge.SyncNode(union);
            controlNetOutput = union.CONTROLNET;
        }
        BridgeSync.SyncLastId(g);

        string applyNode = CreateApplyNode(
            g,
            genInfo,
            controlnetParams,
            controlImage,
            controlModel,
            controlNetOutput,
            controlStrength);
        genInfo.PosCond = new JArray(applyNode, 0);
        genInfo.NegCond = new JArray(applyNode, 1);
    }

    public static bool ConsumeNeedsLtxIcloraGuideCrop(WorkflowGenerator g)
    {
        if (g is null || !g.NodeHelpers.TryGetValue(NeedsLtxIcloraGuideCropKey, out string value))
        {
            return false;
        }
        _ = g.NodeHelpers.Remove(NeedsLtxIcloraGuideCropKey);
        return value == "1";
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

        JArray frameSource = ResolveFullControlImageSource(g.Workflow, controlImagePath);
        if (!OutputHasVideoUpstream(g.Workflow, frameSource))
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        GetImageSizeNode sizeNode = bridge.AddNode(new GetImageSizeNode());
        if (frameSource is { Count: 2 } && bridge.ResolvePath(frameSource) is INodeOutput src)
        {
            sizeNode.Image.ConnectToUntyped(src);
        }
        bridge.SyncNode(sizeNode);
        BridgeSync.SyncLastId(g);

        framesConnection = new JArray(sizeNode.Id, 2);
        g.NodeHelpers[helperKey] = framesConnection.ToString(Formatting.None);
        return true;
    }

    private static JToken ResolveGuideFrameCount(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        string controlNetSource,
        bool clipLengthFromControlNet)
    {
        if (clipLengthFromControlNet
            && TryCreateCapturedControlImageFrameCount(g, controlNetSource, out JArray framesConnection))
        {
            return framesConnection;
        }
        return genInfo?.Frames is int frames ? new JValue(frames) : null;
    }

    private static bool TryApplyLtxIcloraGuide(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData controlImage,
        double controlStrength,
        JToken frameCount)
    {
        if (g.CurrentMedia?.Path is not JArray { Count: 2 } latentPath
            || controlImage?.Path is not JArray { Count: 2 } controlImagePath
            || genInfo?.PosCond is null
            || genInfo.NegCond is null
            || genInfo.Vae?.Path is not JArray { Count: 2 } vaePath
            || !VideoStageModelCompat.IsLtxV2VideoModel(genInfo.VideoModel)
            || genInfo.Model?.Path is not JArray modelPath
            || !OutputRefIsNodeType<LTXICLoRALoaderModelOnlyNode>(g.Workflow, modelPath))
        {
            return false;
        }

        if (controlStrength <= 0)
        {
            return true;
        }
        if (!g.Features.Contains(Constants.LtxVideoFeatureFlag))
        {
            throw new SwarmUserErrorException(
                "VideoStages ControlNet IC-LoRA guides require the ComfyUI-LTXVideo custom nodes. "
                + $"Install {Constants.LtxVideoNodeUrl} or use SwarmUI's LTXVideo feature installer.");
        }

        JArray guideImagePath = ControlImageForLtxIcloraGuide(g, controlImagePath, frameCount);

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LTXAddVideoICLoRAGuideNode guide = bridge.AddNode(new LTXAddVideoICLoRAGuideNode());
        if (bridge.ResolvePath(genInfo.PosCond) is INodeOutput pos) { guide.PositiveInput.ConnectToUntyped(pos); }
        if (bridge.ResolvePath(genInfo.NegCond) is INodeOutput neg) { guide.NegativeInput.ConnectToUntyped(neg); }
        if (bridge.ResolvePath(vaePath) is INodeOutput vae) { guide.Vae.ConnectToUntyped(vae); }
        if (bridge.ResolvePath(latentPath) is INodeOutput latent) { guide.LatentInput.ConnectToUntyped(latent); }
        if (bridge.ResolvePath(guideImagePath) is INodeOutput img) { guide.Image.ConnectToUntyped(img); }
        guide.FrameIdx.Set(0L);
        guide.Strength.Set(controlStrength);
        guide.LatentDownscaleFactor.Set(2.0);
        guide.Crop.Set("disabled");
        guide.UseTiledEncode.Set(false);
        guide.TileSize.Set(256L);
        guide.TileOverlap.Set(64L);
        bridge.SyncNode(guide);
        BridgeSync.SyncLastId(g);

        g.NodeHelpers[NeedsLtxIcloraGuideCropKey] = "1";
        genInfo.PosCond = new JArray(guide.Id, 0);
        genInfo.NegCond = new JArray(guide.Id, 1);
        g.CurrentMedia = g.CurrentMedia.WithPath(
            new JArray(guide.Id, 2),
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat);
        return true;
    }

    private static JArray ControlImageForLtxIcloraGuide(WorkflowGenerator g, JArray controlImagePath, JToken frames)
    {
        if (frames is null || !OutputHasVideoUpstream(g.Workflow, controlImagePath))
        {
            return new JArray(controlImagePath[0], controlImagePath[1]);
        }

        JArray guideSource = ResolveFullControlImageSource(g.Workflow, controlImagePath);

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ResizeImageMaskNodeNode resize = bridge.AddNode(new ResizeImageMaskNodeNode());
        if (guideSource is { Count: 2 } && bridge.ResolvePath(guideSource) is INodeOutput src)
        {
            ((INodeInput)resize.Input).ConnectToUntyped(src);
        }
        resize.ResizeType.Set("scale to multiple");
        resize.ScaleMethod.Set("lanczos");
        // `resize_type.multiple` is a variant input keyed off the selected `resize_type`. The
        // codegen does not model these variant keys, so they go through `ExtraInputs`.
        resize.ExtraInputs = new JObject
        {
            ["resize_type.multiple"] = 64,
        };
        bridge.SyncNode(resize);
        BridgeSync.SyncLastId(g);

        string croppedGuide = AddImageFromBatch(g, new JArray(resize.Id, 0), batchIndex: 0, lengthToken: frames.DeepClone());
        return new JArray(croppedGuide, 0);
    }

    private static string AddImageFromBatch(WorkflowGenerator g, JArray imagePath, int batchIndex, long lengthLiteral)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode());
        if (imagePath is { Count: 2 } && bridge.ResolvePath(imagePath) is INodeOutput src)
        {
            node.Image.ConnectToUntyped(src);
        }
        node.BatchIndex.Set(batchIndex);
        node.Length.Set(lengthLiteral);
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);
        return node.Id;
    }

    private static string AddImageFromBatch(WorkflowGenerator g, JArray imagePath, int batchIndex, JToken lengthToken)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode());
        if (imagePath is { Count: 2 } && bridge.ResolvePath(imagePath) is INodeOutput src)
        {
            node.Image.ConnectToUntyped(src);
        }
        node.BatchIndex.Set(batchIndex);
        if (lengthToken is JArray { Count: 2 } lengthRef && bridge.ResolvePath(lengthRef) is INodeOutput lengthSrc)
        {
            node.Length.ConnectToUntyped(lengthSrc);
        }
        else if (lengthToken is JValue v && v.Value is not null)
        {
            node.Length.Set(System.Convert.ToInt64(v.Value));
        }
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);
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

        if (!TryGetSingleImageFromBatchInput(workflow, capturedApplyImageRef, out JArray batchInput))
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
        if (imageRef is not { Count: 2 })
        {
            return false;
        }

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

    private static bool HasConfiguredVideoStages(WorkflowGenerator g)
    {
        T2IParamType type = VideoStagesExtension.VideoStagesJson?.Type;
        return type is not null
            && g.UserInput.TryGetRaw(type, out object rawValue)
            && rawValue is string json
            && !string.IsNullOrWhiteSpace(json)
            && json.Trim() != "[]";
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
        return bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>().Cast<ComfyNode>()
            .Concat(bridge.Graph.NodesOfType<ControlNetInpaintingAliMamaApplyNode>())
            .Concat(bridge.Graph.NodesOfType<QwenImageDiffsynthControlnetNode>())
            .Select(n => (n.Id, (JObject)workflow[n.Id]))
            .OrderBy(t => NodeSortValue(t.Id));
    }

    private static int NodeSortValue(string nodeId)
    {
        return int.TryParse(nodeId, out int id) ? id : int.MaxValue;
    }

    private static bool ControlApplyUsesModel(JObject workflow, JObject applyNode, string controlModelName)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        if (TryGetInputRef(applyNode, "model_patch", out JArray modelPatchRef))
        {
            return InputChainUsesLoader<ModelPatchLoaderNode>(
                bridge,
                modelPatchRef,
                node => node.Name.LiteralAsString(),
                controlModelName);
        }

        if (!TryGetInputRef(applyNode, "control_net", out JArray controlNetRef))
        {
            return false;
        }

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
        if (outputRef is not { Count: 2 })
        {
            return false;
        }
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

    private static string CapturedControlNetImageKey(int index) => $"{CapturedControlNetImageKeyPrefix}{index}";

    private static string CapturedControlNetFrameCountKey(int index) => $"{CapturedControlNetFrameCountKeyPrefix}{index}";

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

    private static string ResolvePreprocessor(WorkflowGenerator g, int index, T2IModel controlModel)
    {
        if (ComfyUIBackendExtension.ControlNetPreprocessorParams[index] is not null
            && g.UserInput.TryGet(ComfyUIBackendExtension.ControlNetPreprocessorParams[index], out string preprocessor))
        {
            return NormalizePreprocessorName(preprocessor);
        }

        string wantedPreproc = controlModel?.Metadata?.Preprocessor;
        string rawFileName = controlModel?.RawFilePath?.Replace('\\', '/')?.AfterLast('/') ?? "";
        string cnName = $"{controlModel?.Name}{rawFileName}".ToLowerFast();
        if (string.IsNullOrWhiteSpace(wantedPreproc))
        {
            if (cnName.Contains("canny"))
            {
                wantedPreproc = "canny";
            }
            else if (cnName.Contains("depth") || cnName.Contains("midas"))
            {
                wantedPreproc = "depth";
            }
            else if (cnName.Contains("sketch"))
            {
                wantedPreproc = "sketch";
            }
            else if (cnName.Contains("scribble"))
            {
                wantedPreproc = "scribble";
            }
            else if (cnName.Contains("pose"))
            {
                wantedPreproc = "pose";
            }
        }

        if (string.IsNullOrWhiteSpace(wantedPreproc))
        {
            return "none";
        }

        string[] procs = [.. ComfyUIBackendExtension.ControlNetPreprocessors.Keys];
        bool getBestFor(string phrase, out string found)
        {
            found = procs.FirstOrDefault(m => m.ToLowerFast().Contains(phrase.ToLowerFast()));
            return found is not null;
        }

        string? result = null;
        if (wantedPreproc == "depth")
        {
            if (!getBestFor("midas-depthmap", out result)
                && !getBestFor("depthmap", out result)
                && !getBestFor("depth", out result)
                && !getBestFor("midas", out result)
                && !getBestFor("zoe", out result)
                && !getBestFor("leres", out result))
            {
                throw new SwarmUserErrorException(
                    "No preprocessor found for depth - please install a Comfy extension that adds eg MiDaS "
                    + "depthmap preprocessors, or select 'None' if using a manual depthmap");
            }
        }
        else if (wantedPreproc == "canny")
        {
            if (!getBestFor("cannyedge", out result) && !getBestFor("canny", out result))
            {
                result = "none";
            }
        }
        else if (wantedPreproc == "sketch")
        {
            if (!getBestFor("sketch", out result)
                && !getBestFor("lineart", out result)
                && !getBestFor("scribble", out result))
            {
                result = "none";
            }
        }
        else if (wantedPreproc == "pose")
        {
            if (!getBestFor("openpose", out result) && !getBestFor("pose", out result))
            {
                result = "none";
            }
        }
        else
        {
            Logs.Verbose($"Wanted preprocessor {wantedPreproc} unrecognized, skipping...");
            result = "none";
        }
        return NormalizePreprocessorName(result);
    }

    private static string NormalizePreprocessorName(string preprocessor)
    {
        if (string.IsNullOrWhiteSpace(preprocessor) || preprocessor.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }
        return preprocessor;
    }

    private static bool TryGetUnionType(WorkflowGenerator g, int index, out string unionType)
    {
        unionType = null;
        return ComfyUIBackendExtension.ControlNetUnionTypeParams[index] is not null
            && g.UserInput.TryGet(ComfyUIBackendExtension.ControlNetUnionTypeParams[index], out unionType);
    }

    private static void ApplyDiffPatchControlNet(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData controlImage,
        T2IModel controlModel,
        double controlStrength)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ModelPatchLoaderNode loader = bridge.AddNode(new ModelPatchLoaderNode());
        loader.Name.Set(controlModel.ToString(g.ModelFolderFormat));
        bridge.SyncNode(loader);

        QwenImageDiffsynthControlnetNode diff = bridge.AddNode(new QwenImageDiffsynthControlnetNode());
        if (genInfo.Model?.Path is JArray modelPath && bridge.ResolvePath(modelPath) is INodeOutput modelOutput)
        {
            diff.Model.ConnectToUntyped(modelOutput);
        }
        diff.ModelPatch.ConnectTo(loader.MODELPATCH);
        if (genInfo.Vae?.Path is JArray vaePath && bridge.ResolvePath(vaePath) is INodeOutput vae)
        {
            diff.Vae.ConnectToUntyped(vae);
        }
        if (controlImage?.Path is JArray imagePath && bridge.ResolvePath(imagePath) is INodeOutput img)
        {
            diff.Image.ConnectToUntyped(img);
        }
        diff.Strength.Set(controlStrength);
        bridge.SyncNode(diff);
        BridgeSync.SyncLastId(g);

        genInfo.Model = genInfo.Model.WithPath(new JArray(diff.Id, 0));
    }

    private static string CreateApplyNode(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        T2IParamTypes.ControlNetParamHolder controlnetParams,
        WGNodeData controlImage,
        T2IModel controlModel,
        NodeOutput<ControlNetType> controlNetOutput,
        double controlStrength)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        double startPercent = g.UserInput.Get(controlnetParams.Start, 0);
        double endPercent = g.UserInput.Get(controlnetParams.End, 1);

        if (controlModel.Metadata?.ModelClassType == "flux.1-dev/controlnet-alimamainpaint")
        {
            if (g.FinalMask is null)
            {
                throw new SwarmUserErrorException("Alimama Inpainting ControlNet requires a mask.");
            }
            ControlNetInpaintingAliMamaApplyNode alimama = bridge.AddNode(new ControlNetInpaintingAliMamaApplyNode());
            if (bridge.ResolvePath(genInfo.PosCond) is INodeOutput pos) { alimama.PositiveInput.ConnectToUntyped(pos); }
            if (bridge.ResolvePath(genInfo.NegCond) is INodeOutput neg) { alimama.NegativeInput.ConnectToUntyped(neg); }
            alimama.ControlNet.ConnectTo(controlNetOutput);
            if (genInfo.Vae?.Path is JArray vaePath && bridge.ResolvePath(vaePath) is INodeOutput vae) { alimama.Vae.ConnectToUntyped(vae); }
            if (controlImage?.Path is JArray imagePath && bridge.ResolvePath(imagePath) is INodeOutput img) { alimama.Image.ConnectToUntyped(img); }
            if (g.FinalMask is JArray maskPath && bridge.ResolvePath(maskPath) is INodeOutput mask) { alimama.Mask.ConnectToUntyped(mask); }
            alimama.Strength.Set(controlStrength);
            alimama.StartPercent.Set(startPercent);
            alimama.EndPercent.Set(endPercent);
            bridge.SyncNode(alimama);
            BridgeSync.SyncLastId(g);
            return alimama.Id;
        }

        ControlNetApplyAdvancedNode apply = bridge.AddNode(new ControlNetApplyAdvancedNode());
        if (bridge.ResolvePath(genInfo.PosCond) is INodeOutput posCond) { apply.PositiveInput.ConnectToUntyped(posCond); }
        if (bridge.ResolvePath(genInfo.NegCond) is INodeOutput negCond) { apply.NegativeInput.ConnectToUntyped(negCond); }
        apply.ControlNet.ConnectTo(controlNetOutput);
        if (controlImage?.Path is JArray applyImagePath && bridge.ResolvePath(applyImagePath) is INodeOutput applyImg) { apply.Image.ConnectToUntyped(applyImg); }
        apply.Strength.Set(controlStrength);
        apply.StartPercent.Set(startPercent);
        apply.EndPercent.Set(endPercent);
        if (g.IsSD3() || g.IsFlux() || g.IsAnyFlux2() || g.IsChroma() || g.IsQwenImage())
        {
            if (genInfo.Vae?.Path is JArray applyVaePath && bridge.ResolvePath(applyVaePath) is INodeOutput applyVae) { apply.Vae.ConnectToUntyped(applyVae); }
        }
        bridge.SyncNode(apply);
        BridgeSync.SyncLastId(g);
        return apply.Id;
    }
}
