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

namespace VideoStages;

internal class IcLoraApplicator(WorkflowGenerator g)
{
    private const string UploadedDriveImagesKeyPrefix = "videostages.iclora.upload.";
    internal const string UploadedDriveAudioKeyPrefix = "videostages.iclora.uploadaudio.";
    private const string ControlSignalKeyPrefix = "videostages.iclora.control.";

    /// <summary>
    /// Applies the clip's IC-LoRAs to this stage: a loader chain on the model (array order), then
    /// one guide per entry that has drive media — uploaded per-entry, the stage's own input frames
    /// ("Stage Input" = previous stage's output), or a captured core "ControlNet N" branch. An
    /// entry with Stage >= 0 only applies on that stage index. Entries without drive media stay
    /// loader-only (e.g. HDR, text-driven use). Non-slot drive media is resized to the stage's
    /// dimensions before the guide (matching the official IC-LoRA workflows). Each guide's
    /// latent_downscale_factor is wired from its own loader's metadata output, and an entry with
    /// AttentionStrength below 1 uses the Advanced guide node. Returns true when any guide
    /// extended the latent (the caller then crops guide frames after the sampler).
    /// </summary>
    public bool ApplyIcLoras(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ClipSpec clip,
        double? stageControlNetStrength,
        int? frameCount,
        bool clipLengthFromControlNet = false,
        int stageIndex = 0,
        WGNodeData stageInput = null)
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
            if (clip.IcLoras[i].Stage >= 0 && clip.IcLoras[i].Stage != stageIndex)
            {
                continue;
            }
            T2IModel lora = ResolveIcLoraEntryModel(clip.IcLoras[i]);
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
            if (!TryResolveDriveImages(bridge, clip, entry, entryIdx, stageInput, out JArray driveImages, out string slotSource))
            {
                continue;
            }
            double strength = stageControlNetStrength ?? ResolveSlotGuideStrength(slotSource);
            if (strength <= 0)
            {
                continue;
            }
            JArray controlImages = ApplyControlSignal(bridge, clip, entry, entryIdx, driveImages);
            if (slotSource is null)
            {
                controlImages = ResizeToStageDims(bridge, controlImages, genInfo);
            }
            JToken guideFrames = ResolveGuideFrameCount(
                genInfo,
                frameCount,
                slotSource,
                clipLengthFromControlNet && slotSource is not null);
            bool stillImageDrive = slotSource is null
                && StringUtils.Equals(entry.Source, Constants.IcLoraSourceUpload)
                && entry.Video?.Data is string uploadData
                && VideoGraphHelpers.IsImageDataUri(uploadData);
            ApplyLtxIcloraGuide(
                bridge, genInfo, entry, loaders[i], controlImages, strength, guideFrames, stillImageDrive);
            anyGuide = true;
        }
        return anyGuide;
    }

    /// <summary>
    /// The largest known reference-downscale factor among the clip's IC-LoRA entries applicable to
    /// <paramref name="stageIndex"/>. The guide node hard-errors unless the video latent's spatial
    /// dims are divisible by this factor — i.e. pixel dims must be multiples of 32×factor (the
    /// official workflows snap dims via a math node wired from the loader's metadata output). The
    /// true value lives in safetensors metadata only readable graph-side, so this is a static
    /// preset-id/filename-convention lookup; unrecognized custom LoRAs return 1.
    /// </summary>
    internal static int MaxKnownIcLoraDownscaleFactor(ClipSpec clip, int stageIndex)
    {
        int max = 1;
        foreach (IcLoraSpec entry in clip?.IcLoras ?? [])
        {
            if (entry.Stage < 0 || entry.Stage == stageIndex)
            {
                max = Math.Max(max, KnownIcLoraDownscaleFactor(entry));
            }
        }
        return max;
    }

    private static int KnownIcLoraDownscaleFactor(IcLoraSpec entry)
    {
        string name = $"{entry.Preset} {entry.Lora}".ToLowerInvariant();
        if (name.Contains("upscaler-x4"))
        {
            return 4;
        }
        if (name.Contains("ref0.5")
            || name.Contains("union-control")
            || name.Contains("motion-track")
            || name.Contains("upscaler-x2"))
        {
            return 2;
        }
        return 1;
    }

    private bool TryResolveDriveImages(
        WorkflowBridge bridge,
        ClipSpec clip,
        IcLoraSpec entry,
        int entryIdx,
        WGNodeData stageInput,
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
        if (StringUtils.Equals(entry.Source, Constants.IcLoraSourceStageInput))
        {
            if (entry.Stage < 1)
            {
                throw new SwarmUserErrorException(
                    "An IC-LoRA uses the Stage Input drive source but is not applied to a refine "
                    + "stage. Set 'Apply on' to Stage 1 or later, or switch the source to Upload.");
            }
            if (stageInput is null
                || (stageInput.DataType != WGNodeData.DT_IMAGE
                    && stageInput.DataType != WGNodeData.DT_VIDEO))
            {
                throw new SwarmUserErrorException(
                    $"IC-LoRA Stage Input is not available on stage {entry.Stage}: the stage has "
                    + "no image-stream input. Use an uploaded drive video instead.");
            }
            images = new JArray(stageInput.Path[0], stageInput.Path[1]);
            return true;
        }
        if (!ControlNetCapture.TryParseControlNetSourceIndex(entry.Source, out int index)
            || !new ControlNetCapture(g).TryGetCapturedCoreControlImage(index, out WGNodeData controlImage))
        {
            return false;
        }
        slotSource = entry.Source;
        images = new JArray(controlImage.Path[0], controlImage.Path[1]);
        return true;
    }

    // The official IC-LoRA workflows resize the drive to the generation dimensions before the
    // guide, per stage (each stage of a multi-stage flow re-adds the guide at its own size).
    private JArray ResizeToStageDims(
        WorkflowBridge bridge,
        JArray images,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (genInfo?.Width is null || genInfo.Height is null)
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

    // The load + component split for an uploaded drive video (or a still image — e.g. an
    // Ingredients reference sheet) is created once per clip entry and reused by every stage's
    // guide (each stage only adds its own frame-count trim).
    internal JArray GetOrCreateUploadedDriveImages(
        WorkflowBridge bridge,
        int clipId,
        int entryIdx,
        UploadedAudioSpec video)
    {
        string key = $"{UploadedDriveImagesKeyPrefix}{clipId}.{entryIdx}";
        if (VideoGraphHelpers.TryGetCachedPath(g, bridge, key, out JArray cached))
        {
            return cached;
        }
        JArray path;
        if (VideoGraphHelpers.IsImageDataUri(video.Data))
        {
            SwarmLoadImageB64Node loadImage = bridge.AddNode(new SwarmLoadImageB64Node().With(
                ImageBase64: VideoGraphHelpers.StripDataUriPrefix(video.Data)));
            bridge.SyncNode(loadImage);
            path = WorkflowBridge.ToPath(loadImage.IMAGE);
        }
        else
        {
            SwarmLoadVideoB64Node load = bridge.AddNode(new SwarmLoadVideoB64Node().With(
                VideoBase64: VideoGraphHelpers.StripDataUriPrefix(video.Data)));
            GetVideoComponentsNode components = bridge.AddNode(new GetVideoComponentsNode());
            components.Video.ConnectToUntyped(load.VIDEO);
            bridge.SyncNode(load);
            bridge.SyncNode(components);
            path = WorkflowBridge.ToPath(components.Images);
            VideoGraphHelpers.CachePath(
                g,
                $"{UploadedDriveAudioKeyPrefix}{clipId}.{entryIdx}",
                WorkflowBridge.ToPath(components.Audio));
        }
        VideoGraphHelpers.CachePath(g, key, path);
        return path;
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
        if (VideoGraphHelpers.TryGetCachedPath(g, bridge, key, out JArray cached))
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
            LoadDA3ModelNode da3Model = bridge.AddNode(new LoadDA3ModelNode().With(
                ModelName: Constants.Da3ModelFileName));
            DA3InferenceNode inference = bridge.AddNode(new DA3InferenceNode().With(
                Mode: "mono"));
            inference.Da3Model.ConnectToUntyped(da3Model.DA3MODEL);
            inference.Image.TryConnectFromPath(bridge, driveImages);
            DA3RenderNode render = new DA3RenderNode().With(Output: "depth");
            render.ExtraInputs = new JObject
            {
                ["output.normalization"] = "v2_style",
                ["output.apply_sky_clip"] = false,
            };
            bridge.AddNode(render);
            render.Da3Geometry.ConnectToUntyped(inference.Da3Geometry);
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
        VideoGraphHelpers.CachePath(g, key, processed);
        return processed;
    }

    private JToken ResolveGuideFrameCount(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int? stageClipFrames,
        string controlNetSource,
        bool clipLengthFromControlNet)
    {
        if (clipLengthFromControlNet
            && new ControlNetCapture(g).TryCreateCapturedControlImageFrameCount(controlNetSource, out JArray framesConnection))
        {
            return framesConnection;
        }
        int? frames = stageClipFrames ?? genInfo?.Frames;
        return frames is int n ? new JValue(n) : null;
    }

    // Shared config of the basic and Advanced IC-LoRA guide nodes (the Advanced
    // node is the basic one plus per-guide attention strength).
    private const int GuideFrameIdx = 0;
    private const string GuideCrop = "disabled";
    private const bool GuideUseTiledEncode = false;
    private const int GuideTileSize = 256;
    private const int GuideTileOverlap = 64;

    private void ApplyLtxIcloraGuide(
        WorkflowBridge bridge,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        IcLoraSpec entry,
        LTXICLoRALoaderModelOnlyNode loader,
        JArray controlImages,
        double strength,
        JToken frameCount,
        bool stillImageDrive = false)
    {
        JArray guideImagePath = ControlImageForLtxIcloraGuide(
            bridge, controlImages, frameCount, stillImageDrive);

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
                    FrameIdx: GuideFrameIdx,
                    Strength: strength,
                    Crop: GuideCrop,
                    UseTiledEncode: GuideUseTiledEncode,
                    TileSize: GuideTileSize,
                    TileOverlap: GuideTileOverlap,
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
                    FrameIdx: GuideFrameIdx,
                    Strength: strength,
                    Crop: GuideCrop,
                    UseTiledEncode: GuideUseTiledEncode,
                    TileSize: GuideTileSize,
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

    /// <summary>
    /// Resolves an entry's LoRA model, expanding the "[AUTO]" sentinel to the preset's
    /// conventional download path (IcLoraWeights.ModelNameFor — where the [AUTO] downloader
    /// puts the weights). [AUTO] failures throw user errors instead of the plain
    /// resolver's log-and-skip: a silent skip would look like the preset just didn't work.
    /// </summary>
    private static T2IModel ResolveIcLoraEntryModel(IcLoraSpec entry)
    {
        if (!StringUtils.Equals(entry.Lora?.Trim(), Constants.IcLoraAutoModel))
        {
            return ResolveLoraModel(entry.Lora);
        }
        string preset = entry.Preset?.Trim();
        if (string.IsNullOrWhiteSpace(preset) || StringUtils.Equals(preset, "custom"))
        {
            throw new SwarmUserErrorException(
                "An IC-LoRA is set to [AUTO] but has no preset selected. "
                + "Pick a preset (which names the weights to download) or choose a specific LoRA.");
        }
        string autoName = IcLoraWeights.ModelNameFor(preset)
            ?? throw new SwarmUserErrorException(
                $"IC-LoRA [AUTO] preset '{preset}' has no known weights to download. "
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

    private JArray ControlImageForLtxIcloraGuide(
        WorkflowBridge bridge,
        JArray controlImagePath,
        JToken frames,
        bool stillImageDrive = false)
    {
        if (frames is null)
        {
            return new JArray(controlImagePath[0], controlImagePath[1]);
        }

        // A still image (e.g. an Ingredients reference sheet) is REPEATED to the clip's frame
        // count — the official Ingredients workflow tiles the sheet across the full video length
        // so the reference occupies every temporal position. The ImageFromBatch trim below would
        // clamp a 1-frame batch to a single guide frame instead.
        if (stillImageDrive)
        {
            RepeatImageBatchNode repeat = bridge.AddNode(new RepeatImageBatchNode());
            repeat.Image.TryConnectFromPath(
                bridge, new JArray(controlImagePath[0], controlImagePath[1]));
            repeat.Amount.SetFromToken(bridge, frames.DeepClone());
            bridge.SyncNode(repeat);
            return WorkflowBridge.ToPath(repeat.IMAGE);
        }

        JArray guideSource = ControlNetCapture.PeelSingleFrameWrap(bridge, controlImagePath);
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode()).With(
            BatchIndex: 0);
        node.Image.TryConnectFromPath(bridge, guideSource);
        node.Length.SetFromToken(bridge, frames.DeepClone());
        bridge.SyncNode(node);
        return WorkflowBridge.ToPath(node.IMAGE);
    }

    private double ResolveSlotGuideStrength(string slotSource)
    {
        if (slotSource is not null
            && ControlNetCapture.TryParseControlNetSourceIndex(slotSource, out int index)
            && g.UserInput.TryGet(T2IParamTypes.Controlnets[index].Strength, out double slotStrength))
        {
            return slotStrength;
        }
        return 1.0;
    }
}
