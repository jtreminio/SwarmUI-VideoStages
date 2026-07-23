using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Applies the already-compiled IC-LoRA stage plan. Parsing, stage scoping, drive-source
/// classification, control-mode classification, and guide-strength selection are deliberately
/// owned by <see cref="VideoExecutionPlanCompiler"/>, not this graph builder.
/// </summary>
internal sealed class IcLoraApplicator(WorkflowGenerator g)
{
    internal bool ApplyIcLoras(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ClipPlan clip,
        StagePlan stage,
        int? frameCount,
        WGNodeData stageInput)
    {
        if (stage.IcLoras.IsDefaultOrEmpty
            || genInfo.Model is null
            || genInfo.VideoModel.ModelClass.CompatClass.ID != T2IModelClassSorter.CompatLtxv2.ID)
        {
            return false;
        }

        List<ResolvedIcLoraModel> resolved = IcLoraModelResolver.Resolve(stage.IcLoras);
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
        foreach (ResolvedIcLoraModel entry in resolved)
        {
            g.FinalLoadedModelList.Add(entry.Model);
            if (Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash)
            {
                entry.Model.GetOrGenerateTensorHashSha256();
            }
            LTXICLoRALoaderModelOnlyNode loader = bridge.AddNode(new LTXICLoRALoaderModelOnlyNode()).With(
                LoraName: entry.Model.ToString(g.ModelFolderFormat),
                StrengthModel: entry.Plan.ModelStrength);
            if (genInfo.Model.Path is JArray modelPath)
            {
                loader.ModelInput.ConnectFromPath(bridge, modelPath);
            }
            bridge.SyncNode(loader);
            genInfo.Model = genInfo.Model.WithPath(loader.Model);
            loaders.Add(loader);
        }

        IcLoraDriveMediaResolver driveResolver = new(g);
        IcLoraControlSignalBuilder controlSignals = new(g);
        LtxIcLoraGuideApplicator guides = new(g);
        bool anyGuide = false;
        for (int i = 0; i < resolved.Count; i++)
        {
            ResolvedIcLoraModel entry = resolved[i];
            if (!driveResolver.TryResolve(
                    bridge, clip, stage, entry.Plan, stageInput, out ResolvedIcLoraDrive drive))
            {
                continue;
            }

            double strength = entry.Plan.GuideStrength
                ?? ResolveControlNetGuideStrength(entry.Plan.Drive.ControlNetIndex);
            if (strength <= 0)
            {
                continue;
            }

            JArray controlImages = controlSignals.Apply(bridge, clip.ClipId, entry.Plan, drive.Images);
            if (drive.ControlNetIndex is null)
            {
                controlImages = driveResolver.ResizeToStageDimensions(bridge, controlImages, genInfo);
            }
            JToken guideFrames = ResolveGuideFrameCount(
                genInfo,
                frameCount,
                clip.Audio.Length.Owner == AudioLengthOwner.ControlNet,
                drive.ControlNetIndex);
            guides.Apply(
                bridge,
                genInfo,
                entry.Plan,
                loaders[i],
                controlImages,
                strength,
                guideFrames,
                drive.IsStillImage);
            anyGuide = true;
        }
        return anyGuide;
    }

    internal static int MaxKnownIcLoraDownscaleFactor(IEnumerable<IcLoraPlan> plans)
    {
        int max = 1;
        foreach (IcLoraPlan plan in plans ?? [])
        {
            string name = $"{plan.Preset} {plan.ModelName}".ToLowerInvariant();
            if (name.Contains("upscaler-x4"))
            {
                max = Math.Max(max, 4);
            }
            else if (name.Contains("ref0.5")
                || name.Contains("union-control")
                || name.Contains("motion-track")
                || name.Contains("upscaler-x2"))
            {
                max = Math.Max(max, 2);
            }
        }
        return max;
    }

    private JToken ResolveGuideFrameCount(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int? stageClipFrames,
        bool clipLengthFromControlNet,
        int? controlNetIndex)
    {
        if (clipLengthFromControlNet
            && controlNetIndex is int index
            && new ControlNetCapture(g).TryCreateCapturedControlImageFrameCount(index, out JArray framesConnection))
        {
            return framesConnection;
        }
        int? frames = stageClipFrames ?? genInfo.Frames;
        return frames is int n ? new JValue(n) : null;
    }

    private double ResolveControlNetGuideStrength(int? controlNetIndex)
    {
        if (controlNetIndex is int index
            && index >= 0
            && index < T2IParamTypes.Controlnets.Length
            && g.UserInput.TryGet(T2IParamTypes.Controlnets[index].Strength, out double slotStrength))
        {
            return slotStrength;
        }
        return 1.0;
    }
}

internal sealed record ResolvedIcLoraModel(IcLoraPlan Plan, T2IModel Model);

/// <summary>Centralized model validation for compiled IC-LoRA model identities.</summary>
internal static class IcLoraModelResolver
{
    internal static List<ResolvedIcLoraModel> Resolve(IEnumerable<IcLoraPlan> plans)
    {
        List<ResolvedIcLoraModel> resolved = [];
        foreach (IcLoraPlan plan in plans ?? [])
        {
            T2IModel model = Resolve(plan);
            if (model is not null)
            {
                resolved.Add(new(plan, model));
            }
        }
        return resolved;
    }

    private static T2IModel Resolve(IcLoraPlan plan)
    {
        if (!plan.UsesAutoModel)
        {
            return ResolveLoraModel(plan.ModelName);
        }
        if (string.IsNullOrWhiteSpace(plan.Preset) || StringUtils.Equals(plan.Preset, "custom"))
        {
            throw new SwarmUserErrorException(
                "An IC-LoRA is set to [AUTO] but has no preset selected. "
                + "Pick a preset (which names the weights to download) or choose a specific LoRA.");
        }
        string autoName = IcLoraWeights.ModelNameFor(plan.Preset)
            ?? throw new SwarmUserErrorException(
                $"IC-LoRA [AUTO] preset '{plan.Preset}' has no known weights to download. "
                + "Pick a curated preset or choose a specific LoRA.");
        return ResolveLoraModel(autoName)
            ?? throw new SwarmUserErrorException(
                $"IC-LoRA [AUTO] weights '{autoName}' are not installed. The automatic download "
                + "may still be running — wait for it to finish in the timeline editor, or select "
                + "the LoRA manually.");
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
}

internal sealed record ResolvedIcLoraDrive(
    JArray Images,
    int? ControlNetIndex,
    bool IsStillImage);

/// <summary>Resolves planned drive identities into graph media and materializes embedded uploads.</summary>
internal sealed class IcLoraDriveMediaResolver(WorkflowGenerator g)
{
    private const string UploadedDriveImagesKeyPrefix = "videostages.iclora.upload.";
    internal const string UploadedDriveAudioKeyPrefix = "videostages.iclora.uploadaudio.";

    internal bool TryResolve(
        WorkflowBridge bridge,
        ClipPlan clip,
        StagePlan stage,
        IcLoraPlan entry,
        WGNodeData stageInput,
        out ResolvedIcLoraDrive drive)
    {
        drive = null;
        switch (entry.Drive.Kind)
        {
            case IcLoraDriveSourceKind.UploadedMedia:
            {
                JArray images = GetOrCreateUploadedDriveImages(bridge, clip.ClipId, entry);
                if (images is null)
                {
                    return false;
                }
                drive = new(images, null, entry.Drive.UploadedMediaKind == IcLoraUploadedMediaKind.Image);
                return true;
            }
            case IcLoraDriveSourceKind.StageInput:
                if (stage.ClipStageRawIndex < 1 && !clip.IsSourced)
                {
                    throw new SwarmUserErrorException(
                        "An IC-LoRA uses the Stage Input drive source but is not applied to a refine "
                        + "stage. Set 'Apply on' to Stage 1 or later, or switch the source to Upload.");
                }
                if (stageInput is null || !IsImageStream(stageInput))
                {
                    throw new SwarmUserErrorException(
                        $"VideoStages: planned IC-LoRA Stage Input drive is unavailable for stage "
                        + $"{stage.ClipStageRawIndex}. Regenerate after updating the timeline or upload drive media.");
                }
                drive = new(new JArray(stageInput.Path[0], stageInput.Path[1]), null, false);
                return true;
            case IcLoraDriveSourceKind.SourcedClipInput:
                if (stageInput is null || !IsImageStream(stageInput))
                {
                    Logs.Warning(
                        $"VideoStages: planned IC-LoRA entry {entry.EntryIndex} requires sourced clip input "
                        + "media, but it is unavailable; applying the model patch without a guide.");
                    return false;
                }
                drive = new(new JArray(stageInput.Path[0], stageInput.Path[1]), null, false);
                return true;
            case IcLoraDriveSourceKind.ControlNet:
                if (entry.Drive.ControlNetIndex is not int index
                    || !new ControlNetCapture(g).TryGetCapturedCoreControlImage(index, out WGNodeData controlImage))
                {
                    Logs.Warning(
                        $"VideoStages: planned IC-LoRA entry {entry.EntryIndex} requires ControlNet "
                        + $"{(entry.Drive.ControlNetIndex ?? -1) + 1} drive media, but it is unavailable; "
                        + "applying the model patch without a guide.");
                    return false;
                }
                drive = new(new JArray(controlImage.Path[0], controlImage.Path[1]), index, false);
                return true;
            case IcLoraDriveSourceKind.LoaderOnly:
                return false;
            default:
                Logs.Warning(
                    $"VideoStages: planned IC-LoRA entry {entry.EntryIndex} has no usable drive-media "
                    + "identity; applying the model patch without a guide.");
                return false;
        }
    }

    internal JArray GetOrCreateUploadedDriveImages(
        WorkflowBridge bridge,
        int clipId,
        IcLoraPlan entry) => GetOrCreateUploadedDriveImages(
            bridge,
            clipId,
            entry.EntryIndex,
            entry.Drive.UploadedMediaKind,
            entry.Drive.UploadedData);

    internal JArray GetOrCreateUploadedDriveImages(
        WorkflowBridge bridge,
        int clipId,
        int entryIndex,
        IcLoraUploadedMediaKind mediaKind,
        string data)
    {
        string key = $"{UploadedDriveImagesKeyPrefix}{clipId}.{entryIndex}";
        if (VideoGraphHelpers.TryGetCachedPath(g, bridge, key, out JArray cached))
        {
            return cached;
        }
        if (string.IsNullOrWhiteSpace(data))
        {
            Logs.Warning(
                $"VideoStages: planned IC-LoRA entry {entryIndex} requires uploaded drive media, "
                + "but the planned media identity is empty; applying the model patch without a guide.");
            return null;
        }

        JArray path;
        if (mediaKind == IcLoraUploadedMediaKind.Image)
        {
            SwarmLoadImageB64Node loadImage = bridge.AddNode(new SwarmLoadImageB64Node().With(
                ImageBase64: VideoGraphHelpers.StripDataUriPrefix(data)));
            bridge.SyncNode(loadImage);
            path = WorkflowBridge.ToPath(loadImage.IMAGE);
        }
        else if (mediaKind == IcLoraUploadedMediaKind.Video)
        {
            SwarmLoadVideoB64Node load = bridge.AddNode(new SwarmLoadVideoB64Node().With(
                VideoBase64: VideoGraphHelpers.StripDataUriPrefix(data)));
            GetVideoComponentsNode components = bridge.AddNode(new GetVideoComponentsNode());
            components.Video.ConnectToUntyped(load.VIDEO);
            bridge.SyncNode(load);
            bridge.SyncNode(components);
            path = WorkflowBridge.ToPath(components.Images);
            VideoGraphHelpers.CachePath(
                g,
                $"{UploadedDriveAudioKeyPrefix}{clipId}.{entryIndex}",
                WorkflowBridge.ToPath(components.Audio));
        }
        else
        {
            Logs.Warning(
                $"VideoStages: planned IC-LoRA entry {entryIndex} has unsupported uploaded drive-media "
                + "kind; applying the model patch without a guide.");
            return null;
        }
        VideoGraphHelpers.CachePath(g, key, path);
        return path;
    }

    internal JArray ResizeToStageDimensions(
        WorkflowBridge bridge,
        JArray images,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (genInfo.Width is null || genInfo.Height is null)
        {
            return images;
        }
        ResizeImageMaskNodeNode resize = bridge.AddNode(new ResizeImageMaskNodeNode()).With(
            ResizeType: "scale dimensions",
            ScaleMethod: "lanczos");
        resize.Input.TryConnectFromPath(bridge, images);
        resize.ExtraInputs["resize_type.width"] = genInfo.Width.DeepClone();
        resize.ExtraInputs["resize_type.height"] = genInfo.Height.DeepClone();
        resize.ExtraInputs["resize_type.crop"] = "center";
        bridge.SyncNode(resize);
        return WorkflowBridge.ToPath(resize.Resized);
    }

    private static bool IsImageStream(WGNodeData media) =>
        media.DataType == WGNodeData.DT_IMAGE || media.DataType == WGNodeData.DT_VIDEO;
}

/// <summary>Builds the optional Canny, depth, or normal control signal for one planned drive.</summary>
internal sealed class IcLoraControlSignalBuilder(WorkflowGenerator g)
{
    private const string ControlSignalKeyPrefix = "videostages.iclora.control.";

    internal JArray Apply(WorkflowBridge bridge, int clipId, IcLoraPlan entry, JArray driveImages)
    {
        if (entry.ControlMode == IcLoraControlMode.None)
        {
            return driveImages;
        }
        string key = $"{ControlSignalKeyPrefix}{clipId}.{entry.EntryIndex}";
        if (VideoGraphHelpers.TryGetCachedPath(g, bridge, key, out JArray cached))
        {
            return cached;
        }

        JArray processed = entry.ControlMode switch
        {
            IcLoraControlMode.Canny => BuildCanny(bridge, driveImages),
            IcLoraControlMode.Depth => BuildDepth(bridge, driveImages),
            _ => BuildNormal(bridge, driveImages),
        };
        VideoGraphHelpers.CachePath(g, key, processed);
        return processed;
    }

    private static JArray BuildCanny(WorkflowBridge bridge, JArray images)
    {
        CannyNode canny = bridge.AddNode(new CannyNode());
        canny.Image.TryConnectFromPath(bridge, images);
        bridge.SyncNode(canny);
        return WorkflowBridge.ToPath(canny.IMAGE);
    }

    private static JArray BuildDepth(WorkflowBridge bridge, JArray images)
    {
        LoadDA3ModelNode model = bridge.AddNode(new LoadDA3ModelNode().With(ModelName: Constants.Da3ModelFileName));
        DA3InferenceNode inference = bridge.AddNode(new DA3InferenceNode().With(Mode: "mono"));
        inference.Da3Model.ConnectToUntyped(model.DA3MODEL);
        inference.Image.TryConnectFromPath(bridge, images);
        DA3RenderNode render = new DA3RenderNode().With(Output: "depth");
        render.ExtraInputs = new JObject
        {
            ["output.normalization"] = "v2_style",
            ["output.apply_sky_clip"] = false,
        };
        bridge.AddNode(render);
        render.Da3Geometry.ConnectToUntyped(inference.Da3Geometry);
        bridge.SyncNode(model);
        bridge.SyncNode(inference);
        bridge.SyncNode(render);
        return WorkflowBridge.ToPath(render.IMAGE);
    }

    private static JArray BuildNormal(WorkflowBridge bridge, JArray images)
    {
        LoadMoGeModelNode model = bridge.AddNode(new LoadMoGeModelNode().With(ModelName: Constants.MoGeModelFileName));
        MoGeInferenceNode inference = bridge.AddNode(new MoGeInferenceNode());
        inference.MogeModel.ConnectToUntyped(model.MOGEMODEL);
        inference.Image.TryConnectFromPath(bridge, images);
        MoGeRenderNode render = bridge.AddNode(new MoGeRenderNode().With(Output: "normal_opengl"));
        render.MogeGeometry.ConnectToUntyped(inference.MogeGeometry);
        bridge.SyncNode(model);
        bridge.SyncNode(inference);
        bridge.SyncNode(render);
        return WorkflowBridge.ToPath(render.IMAGE);
    }
}

/// <summary>Applies LTX IC-LoRA guide nodes after drive media and control signals are resolved.</summary>
internal sealed class LtxIcLoraGuideApplicator(WorkflowGenerator g)
{
    private const int GuideFrameIdx = 0;
    private const string GuideCrop = "disabled";
    private const bool GuideUseTiledEncode = false;
    private const int GuideTileSize = 256;
    private const int GuideTileOverlap = 64;

    internal void Apply(
        WorkflowBridge bridge,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        IcLoraPlan entry,
        LTXICLoRALoaderModelOnlyNode loader,
        JArray controlImages,
        double strength,
        JToken frameCount,
        bool stillImageDrive)
    {
        JArray guideImagePath = PrepareGuideFrames(bridge, controlImages, frameCount, stillImageDrive);
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
                    FrameIdx: GuideFrameIdx, Strength: strength, Crop: GuideCrop,
                    UseTiledEncode: GuideUseTiledEncode, TileSize: GuideTileSize,
                    TileOverlap: GuideTileOverlap, AttentionStrength: entry.AttentionStrength));
            guideNode = advanced;
            vae = advanced.Vae;
            latentInput = advanced.LatentInput;
            image = advanced.Image;
            downscale = advanced.LatentDownscaleFactor;
            latentOut = advanced.Latent;
        }
        else
        {
            LTXAddVideoICLoRAGuideNode basic = bridge.AddNode(new LTXAddVideoICLoRAGuideNode().With(
                FrameIdx: GuideFrameIdx, Strength: strength, Crop: GuideCrop,
                UseTiledEncode: GuideUseTiledEncode, TileSize: GuideTileSize,
                TileOverlap: GuideTileOverlap));
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
        downscale.ConnectToUntyped(loader.LatentDownscaleFactor);
        bridge.SyncNode(guideNode);
        genInfo.SetConditioning(guide);
        g.CurrentMedia = g.CurrentMedia.WithPath(latentOut, WGNodeData.DT_LATENT_VIDEO, genInfo.Model.Compat);
    }

    private static JArray PrepareGuideFrames(
        WorkflowBridge bridge,
        JArray controlImagePath,
        JToken frames,
        bool stillImageDrive)
    {
        if (frames is null)
        {
            return new JArray(controlImagePath[0], controlImagePath[1]);
        }
        if (stillImageDrive)
        {
            RepeatImageBatchNode repeat = bridge.AddNode(new RepeatImageBatchNode());
            repeat.Image.TryConnectFromPath(bridge, new JArray(controlImagePath[0], controlImagePath[1]));
            repeat.Amount.SetFromToken(bridge, frames.DeepClone());
            bridge.SyncNode(repeat);
            return WorkflowBridge.ToPath(repeat.IMAGE);
        }

        JArray guideSource = ControlNetCapture.PeelSingleFrameWrap(bridge, controlImagePath);
        ImageFromBatchNode trim = bridge.AddNode(new ImageFromBatchNode().With(BatchIndex: 0));
        trim.Image.TryConnectFromPath(bridge, guideSource);
        trim.Length.SetFromToken(bridge, frames.DeepClone());
        bridge.SyncNode(trim);
        return WorkflowBridge.ToPath(trim.IMAGE);
    }
}
