using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using ComfyTyped.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;

namespace VideoStages;

internal class ControlNetApplicator(WorkflowGenerator g)
{
    private const string CapturedControlNetImageKeyPrefix = "videostages.controlnet.fullimage.";
    private const string CapturedControlNetFrameCountKeyPrefix = "videostages.controlnet.framecount.";
    private const string CapturedControlNetAudioKeyPrefix = "videostages.controlnet.audio.";
    private const string UploadedDriveImagesKeyPrefix = "videostages.iclora.upload.";
    private const string ControlSignalKeyPrefix = "videostages.iclora.control.";

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

    /// <summary>
    /// Applies every IC-LoRA on the clip to this stage: a loader chain on the model (array order),
    /// then one guide per entry that has a drive video — uploaded per-entry or a captured core
    /// "ControlNet N" branch. Entries without a drive video stay loader-only (e.g. HDR, text-driven
    /// use). Each guide's latent_downscale_factor is wired from its own loader's metadata output, and
    /// an entry with AttentionStrength below 1 uses the Advanced guide node. Returns true when any
    /// guide extended the latent (the caller then crops guide frames after the sampler).
    /// </summary>
    public bool ApplyIcLoras(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ClipSpec clip,
        double? stageControlNetStrength,
        int? frameCount,
        bool clipLengthFromControlNet = false)
    {
        if (!clip.HasIcLoras
            || genInfo.Model is null
            || genInfo.VideoModel.ModelClass.CompatClass.ID != T2IModelClassSorter.CompatLtxv2.ID)
        {
            return false;
        }

        List<(IcLoraSpec Entry, int EntryIdx, T2IModel Lora)> resolved = [];
        for (int i = 0; i < clip.IcLoras.Count; i++)
        {
            T2IModel lora = ResolveLoraModel(clip.IcLoras[i].Lora);
            if (lora is not null)
            {
                resolved.Add((clip.IcLoras[i], i, lora));
            }
        }
        if (resolved.Count == 0)
        {
            return false;
        }
        if (!g.Features.Contains(Constants.LtxVideoFeatureFlag))
        {
            throw new SwarmUserErrorException(
                "VideoStages IC-LoRAs require the ComfyUI-LTXVideo custom nodes. "
                + $"Install {Constants.LtxVideoNodeUrl} or use SwarmUI's LTXVideo feature installer.");
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        List<LTXICLoRALoaderModelOnlyNode> loaders = [];
        foreach ((IcLoraSpec entry, _, T2IModel lora) in resolved)
        {
            g.FinalLoadedModelList.Add(lora);
            if (Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash)
            {
                lora.GetOrGenerateTensorHashSha256();
            }
            LTXICLoRALoaderModelOnlyNode loader = bridge.AddNode(new LTXICLoRALoaderModelOnlyNode()).With(
                LoraName: lora.ToString(g.ModelFolderFormat),
                StrengthModel: entry.Strength);
            if (genInfo.Model?.Path is JArray modelPath)
            {
                loader.ModelInput.ConnectFromPath(bridge, modelPath);
            }
            bridge.SyncNode(loader);
            genInfo.Model = genInfo.Model.WithPath(loader.Model);
            loaders.Add(loader);
        }

        bool anyGuide = false;
        for (int i = 0; i < resolved.Count; i++)
        {
            (IcLoraSpec entry, int entryIdx, _) = resolved[i];
            if (!TryResolveDriveImages(bridge, clip, entry, entryIdx, out JArray driveImages, out string slotSource))
            {
                continue;
            }
            double strength = stageControlNetStrength ?? ResolveSlotGuideStrength(slotSource);
            if (strength <= 0)
            {
                continue;
            }
            JArray controlImages = ApplyControlSignal(bridge, clip, entry, entryIdx, driveImages);
            JToken guideFrames = ResolveGuideFrameCount(
                genInfo,
                frameCount,
                slotSource,
                clipLengthFromControlNet && slotSource is not null);
            ApplyLtxIcloraGuide(bridge, genInfo, entry, loaders[i], controlImages, strength, guideFrames);
            anyGuide = true;
        }
        return anyGuide;
    }

    private double ResolveSlotGuideStrength(string slotSource)
    {
        if (slotSource is not null
            && TryParseControlNetSourceIndex(slotSource, out int index)
            && g.UserInput.TryGet(T2IParamTypes.Controlnets[index].Strength, out double slotStrength))
        {
            return slotStrength;
        }
        return 1.0;
    }

    private bool TryResolveDriveImages(
        WorkflowBridge bridge,
        ClipSpec clip,
        IcLoraSpec entry,
        int entryIdx,
        out JArray images,
        out string slotSource)
    {
        images = null;
        slotSource = null;
        if (StringUtils.Equals(entry.Source, Constants.IcLoraSourceUpload))
        {
            if (string.IsNullOrWhiteSpace(entry.Video?.Data))
            {
                return false;
            }
            images = GetOrCreateUploadedDriveImages(bridge, clip.Id, entryIdx, entry.Video);
            return images is not null;
        }
        if (!TryParseControlNetSourceIndex(entry.Source, out int index)
            || !TryGetCapturedCoreControlImage(index, out WGNodeData controlImage))
        {
            return false;
        }
        slotSource = entry.Source;
        images = new JArray(controlImage.Path[0], controlImage.Path[1]);
        return true;
    }

    // The load + component split for an uploaded drive video (or a still image — e.g. an
    // Ingredients reference sheet) is created once per clip entry and reused by every stage's
    // guide (each stage only adds its own frame-count trim).
    private JArray GetOrCreateUploadedDriveImages(
        WorkflowBridge bridge,
        int clipId,
        int entryIdx,
        UploadedAudioSpec video)
    {
        string key = $"{UploadedDriveImagesKeyPrefix}{clipId}.{entryIdx}";
        if (g.NodeHelpers.TryGetValue(key, out string encoded)
            && !string.IsNullOrWhiteSpace(encoded)
            && JToken.Parse(encoded) is JArray { Count: 2 } cached
            && bridge.ResolvePath(cached) is not null)
        {
            return cached;
        }
        JArray path;
        if (IsImageDataUri(video.Data))
        {
            SwarmLoadImageB64Node loadImage = bridge.AddNode(new SwarmLoadImageB64Node().With(
                ImageBase64: StripDataUriPrefix(video.Data)));
            bridge.SyncNode(loadImage);
            path = WorkflowBridge.ToPath(loadImage.IMAGE);
        }
        else
        {
            SwarmLoadVideoB64Node load = bridge.AddNode(new SwarmLoadVideoB64Node().With(
                VideoBase64: StripDataUriPrefix(video.Data)));
            GetVideoComponentsNode components = bridge.AddNode(new GetVideoComponentsNode());
            components.Video.ConnectToUntyped(load.VIDEO);
            bridge.SyncNode(load);
            bridge.SyncNode(components);
            path = WorkflowBridge.ToPath(components.Images);
        }
        g.NodeHelpers[key] = path.ToString(Formatting.None);
        return path;
    }

    private static bool IsImageDataUri(string data) =>
        data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

    private static string StripDataUriPrefix(string data)
    {
        int comma = data.IndexOf(',');
        return data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
            ? data[(comma + 1)..]
            : data;
    }

    // Renders the drive video into the entry's control signal (canny edges or MoGe depth/normal maps);
    // cached per clip entry so every stage shares one preprocessing chain.
    private JArray ApplyControlSignal(
        WorkflowBridge bridge,
        ClipSpec clip,
        IcLoraSpec entry,
        int entryIdx,
        JArray driveImages)
    {
        if (StringUtils.Equals(entry.ControlType, Constants.IcLoraControlNone)
            || string.IsNullOrWhiteSpace(entry.ControlType))
        {
            return driveImages;
        }
        string key = $"{ControlSignalKeyPrefix}{clip.Id}.{entryIdx}";
        if (g.NodeHelpers.TryGetValue(key, out string encoded)
            && !string.IsNullOrWhiteSpace(encoded)
            && JToken.Parse(encoded) is JArray { Count: 2 } cached
            && bridge.ResolvePath(cached) is not null)
        {
            return cached;
        }

        JArray processed;
        if (StringUtils.Equals(entry.ControlType, Constants.IcLoraControlCanny))
        {
            CannyNode canny = bridge.AddNode(new CannyNode());
            canny.Image.TryConnectFromPath(bridge, driveImages);
            bridge.SyncNode(canny);
            processed = WorkflowBridge.ToPath(canny.IMAGE);
        }
        else if (StringUtils.Equals(entry.ControlType, Constants.IcLoraControlDepth))
        {
            LoadDa3ModelNode da3Model = bridge.AddNode(new LoadDa3ModelNode());
            da3Model.ModelName.Set(Constants.Da3ModelFileName);
            Da3InferenceNode inference = bridge.AddNode(new Da3InferenceNode());
            inference.Da3Model.ConnectToUntyped(da3Model.Model);
            inference.Image.TryConnectFromPath(bridge, driveImages);
            Da3RenderNode render = new()
            {
                ExtraInputs = new JObject
                {
                    ["output.normalization"] = "v2_style",
                    ["output.apply_sky_clip"] = false,
                },
            };
            bridge.AddNode(render);
            render.Geometry.ConnectToUntyped(inference.Geometry);
            bridge.SyncNode(da3Model);
            bridge.SyncNode(inference);
            bridge.SyncNode(render);
            processed = WorkflowBridge.ToPath(render.IMAGE);
        }
        else
        {
            LoadMoGeModelNode mogeModel = bridge.AddNode(new LoadMoGeModelNode().With(
                ModelName: Constants.MoGeModelFileName));
            MoGeInferenceNode inference = bridge.AddNode(new MoGeInferenceNode());
            inference.MogeModel.ConnectToUntyped(mogeModel.MOGEMODEL);
            inference.Image.TryConnectFromPath(bridge, driveImages);
            MoGeRenderNode render = bridge.AddNode(new MoGeRenderNode().With(
                Output: "normal_opengl"));
            render.MogeGeometry.ConnectToUntyped(inference.MogeGeometry);
            bridge.SyncNode(mogeModel);
            bridge.SyncNode(inference);
            bridge.SyncNode(render);
            processed = WorkflowBridge.ToPath(render.IMAGE);
        }
        g.NodeHelpers[key] = processed.ToString(Formatting.None);
        return processed;
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

    private void ApplyLtxIcloraGuide(
        WorkflowBridge bridge,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        IcLoraSpec entry,
        LTXICLoRALoaderModelOnlyNode loader,
        JArray controlImages,
        double strength,
        JToken frameCount)
    {
        JArray guideImagePath = ControlImageForLtxIcloraGuide(bridge, controlImages, frameCount);

        ComfyNode guideNode;
        NodeInput<VaeType> vae;
        NodeInput<LatentType> latentInput;
        NodeInput<ImageType> image;
        NodeInput<FloatType> downscale;
        NodeOutput<LatentType> latentOut;
        if (entry.AttentionStrength < 1)
        {
            LTXAddVideoICLoRAGuideAdvancedNode advanced =
                bridge.AddNode(new LTXAddVideoICLoRAGuideAdvancedNode().With(
                    FrameIdx: 0,
                    Strength: strength,
                    Crop: "disabled",
                    UseTiledEncode: false,
                    TileSize: 256,
                    TileOverlap: 64,
                    AttentionStrength: entry.AttentionStrength));
            guideNode = advanced;
            vae = advanced.Vae;
            latentInput = advanced.LatentInput;
            image = advanced.Image;
            downscale = advanced.LatentDownscaleFactor;
            latentOut = advanced.Latent;
        }
        else
        {
            LTXAddVideoICLoRAGuideNode basic =
                bridge.AddNode(new LTXAddVideoICLoRAGuideNode().With(
                    FrameIdx: 0,
                    Strength: strength,
                    Crop: "disabled",
                    UseTiledEncode: false,
                    TileSize: 256,
                    TileOverlap: 64));
            guideNode = basic;
            vae = basic.Vae;
            latentInput = basic.LatentInput;
            image = basic.Image;
            downscale = basic.LatentDownscaleFactor;
            latentOut = basic.Latent;
        }

        IConditioningPairNode guide = (IConditioningPairNode)guideNode;
        guide.ConnectConditioning(bridge, genInfo);
        vae.ConnectFromPath(bridge, genInfo.Vae.Path);
        latentInput.ConnectFromPath(bridge, g.CurrentMedia.Path);
        image.ConnectFromPath(bridge, guideImagePath);
        // The IC-LoRA's reference_downscale_factor rides in its safetensors metadata; the loader
        // surfaces it, so the guide always encodes at the grid the LoRA was trained for.
        downscale.ConnectToUntyped(loader.LatentDownscaleFactor);
        bridge.SyncNode(guideNode);

        genInfo.SetConditioning(guide);
        g.CurrentMedia = g.CurrentMedia.WithPath(
            latentOut,
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat);
    }

    internal static T2IModel ResolveLoraModel(string loraName)
    {
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler loraHandler))
        {
            Logs.Error("LoRA models are not available.");
            return null;
        }
        if (!loraHandler.Models.TryGetValue(loraName + ".safetensors", out T2IModel lora)
            && !loraHandler.Models.TryGetValue(loraName, out lora))
        {
            Logs.Error($"LoRA Model '{loraName}' not found in the model set.");
            return null;
        }
        return lora;
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
