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

internal class ControlNetApplicator(WorkflowGenerator g)
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
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
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
        BridgeSync.SyncLastId(g);
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

    public bool Apply(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        string source,
        double? stageControlNetStrength,
        int? frameCount,
        bool clipLengthFromControlNet = false)
    {
        if (string.IsNullOrWhiteSpace(source)
            || genInfo.VideoModel.ModelClass.CompatClass.ID != T2IModelClassSorter.CompatLtxv2.ID)
        {
            return false;
        }

        int index = ParseControlNetSourceIndex(source);
        if (!TryGetCapturedCoreControlImage(index, out WGNodeData controlImage))
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.NodeAt(genInfo.Model.Path) is not LTXICLoRALoaderModelOnlyNode)
        {
            Logs.Warning(
                $"VideoStages: ControlNet '{source}' is configured but no controlnet_lora "
                + "is set for this stage; skipping ControlNet.");
            return false;
        }

        T2IParamTypes.ControlNetParamHolder controlnetParams = T2IParamTypes.Controlnets[index];
        JToken guideFrameCount = ResolveGuideFrameCount(genInfo, frameCount, source, clipLengthFromControlNet);
        double strength = stageControlNetStrength ?? g.UserInput.Get(controlnetParams.Strength);

        return ApplyLtxIcloraGuide(genInfo, controlImage, strength, guideFrameCount);
    }

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
        if (g.NodeHelpers.TryGetValue(helperKey, out string encoded)
            && !string.IsNullOrWhiteSpace(encoded)
            && JToken.Parse(encoded) is JArray { Count: 2 } cached)
        {
            framesConnection = cached;
            return true;
        }

        if (!TryGetCapturedCoreControlImage(index, out WGNodeData controlImage)
            || controlImage.Path is not JArray { Count: 2 } controlImagePath)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
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
        BridgeSync.SyncLastId(g);
        framesConnection = WorkflowBridge.ToPath(sizeNode.BatchSize);
        g.NodeHelpers[helperKey] = framesConnection.ToString(Formatting.None);
        return true;
    }

    private JToken ResolveGuideFrameCount(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int? stageClipFrames,
        string controlNetSource,
        bool clipLengthFromControlNet)
    {
        if (clipLengthFromControlNet
            && TryCreateCapturedControlImageFrameCount(controlNetSource, out JArray framesConnection))
        {
            return framesConnection;
        }
        int? frames = stageClipFrames ?? genInfo?.Frames;
        return frames is int n ? new JValue(n) : null;
    }

    private bool ApplyLtxIcloraGuide(
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

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        JArray guideImagePath = ControlImageForLtxIcloraGuide(bridge, controlImage.Path, frameCount);

        LTXAddVideoICLoRAGuideNode guide = bridge.AddNode(new LTXAddVideoICLoRAGuideNode().With(
            FrameIdx: 0,
            Strength: strength,
            LatentDownscaleFactor: 2.0,
            Crop: "disabled",
            UseTiledEncode: false,
            TileSize: 256,
            TileOverlap: 64));
        guide.ConnectConditioning(bridge, genInfo);
        guide.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
        guide.LatentInput.ConnectFromPath(bridge, g.CurrentMedia.Path);
        guide.Image.ConnectFromPath(bridge, guideImagePath);
        bridge.SyncNode(guide);
        BridgeSync.SyncLastId(g);

        genInfo.SetConditioning(guide);
        g.CurrentMedia = g.CurrentMedia.WithPath(
            guide.Latent,
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat);
        return true;
    }

    private JArray ControlImageForLtxIcloraGuide(
        WorkflowBridge bridge,
        JArray controlImagePath,
        JToken frames)
    {
        if (frames is null)
        {
            return new JArray(controlImagePath[0], controlImagePath[1]);
        }

        JArray guideSource = PeelSingleFrameWrap(bridge, controlImagePath);
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode()).With(
            BatchIndex: 0);
        node.Image.TryConnectFromPath(bridge, guideSource);
        node.Length.SetFromToken(bridge, frames.DeepClone());
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);
        return WorkflowBridge.ToPath(node.IMAGE);
    }

    private static JArray PeelSingleFrameWrap(WorkflowBridge bridge, JArray imagePath)
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
                    || !TryGetInputRef(candidateNode, loaderInputName, out JArray loaderRef)
                    || !TryGetInputRef(candidateNode, "image", out JArray imageInput))
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

    private bool TryGetCapturedCoreControlImage(int index, out WGNodeData controlImage)
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

    private static int ParseControlNetSourceIndex(string controlNetSource)
    {
        if (TryParseControlNetSourceIndex(controlNetSource, out int index))
        {
            return index;
        }
        throw new SwarmUserErrorException($"Unrecognized ControlNet source: '{controlNetSource}'");
    }

    private static bool TryParseControlNetSourceIndex(string controlNetSource, out int index)
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
