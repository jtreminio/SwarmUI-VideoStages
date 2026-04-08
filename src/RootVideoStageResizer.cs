using System;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal static class RootVideoStageResizer
{
    private const string DefaultRootGuideReference = "Default";
    private const string RootGuideReferenceHelperKey = "videostages.root.guide.reference";
    private const string RootGuideLastFrameReferenceHelperKey = "videostages.root.guide.lastframe.reference";
    private const int LtxvGuideImgCompression = 18;
    private const double LegacyDefaultGuideStrength = 0.8;
    internal const double AdditionalStageGuideStrength = 0.5;
    internal const int FirstFrameGuideFrameIndex = 2;
    internal const int LastFrameGuideFrameIndex = -2;
    private static readonly Action<WorkflowGenerator.ImageToVideoGenInfo> PreHandler = ApplyIfNeeded;
    private static readonly Action<WorkflowGenerator.ImageToVideoGenInfo> PostHandler = ApplyPostIfNeeded;

    public static void EnsureRegistered()
    {
        if (!WorkflowGenerator.AltImageToVideoPreHandlers.Contains(PreHandler))
        {
            WorkflowGenerator.AltImageToVideoPreHandlers.Add(PreHandler);
        }
        if (!WorkflowGenerator.AltImageToVideoPostHandlers.Contains(PostHandler))
        {
            WorkflowGenerator.AltImageToVideoPostHandlers.Add(PostHandler);
        }
    }

    private static void ApplyIfNeeded(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator g = genInfo?.Generator;
        if (g is null
            || genInfo.ContextID != T2IParamInput.SectionID_Video)
        {
            return;
        }

        string rootGuideReference = GetNormalizedRootGuideReference(
            g,
            RootGuideReferenceHelperKey,
            VideoStagesExtension.RootGuideImageReference,
            "Root guide image reference");
        string rootLastFrameReference = GetNormalizedRootGuideReference(
            g,
            RootGuideLastFrameReferenceHelperKey,
            VideoStagesExtension.RootGuideLastFrameReference,
            "Root guide last frame reference");
        bool hasRootResolution = TryGetRootStageResolution(g.UserInput, out int width, out int height);
        if (!hasRootResolution
            && IsDefaultRootGuideReference(rootGuideReference)
            && IsDefaultRootGuideReference(rootLastFrameReference))
        {
            return;
        }
        if (HasExplicitRootLastFrameGuideReference(rootLastFrameReference))
        {
            genInfo.VideoEndFrame = null;
        }

        ApplyRootGuideReferenceIfNeeded(genInfo, rootGuideReference);
        if (!hasRootResolution)
        {
            return;
        }

        genInfo.Width = width;
        genInfo.Height = height;
        if (g.CurrentMedia is not null)
        {
            g.CurrentMedia.Width = width;
            g.CurrentMedia.Height = height;
        }

        if (TryUpdateExistingScaleNode(g, imagePath: null, width, height, crop: "center"))
        {
            return;
        }

        string scaleNode = g.CreateNode(NodeTypes.ImageScale, new JObject()
        {
            ["image"] = g.CurrentMedia.Path,
            ["width"] = width,
            ["height"] = height,
            ["upscale_method"] = "lanczos",
            ["crop"] = "center"
        });
        g.CurrentMedia = g.CurrentMedia.WithPath([scaleNode, 0]);
        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
    }

    private static void ApplyRootGuideReferenceIfNeeded(WorkflowGenerator.ImageToVideoGenInfo genInfo, string guideReference)
    {
        WorkflowGenerator g = genInfo?.Generator;
        if (g is null)
        {
            return;
        }

        if (IsDefaultRootGuideReference(guideReference))
        {
            return;
        }

        WGNodeData guideImage = ResolveRootGuideImage(g, guideReference, "Root guide image reference");

        if (TryUpdateExistingScaleNode(g, imagePath: guideImage.Path))
        {
            return;
        }

        string scaleNode = g.CreateNode(NodeTypes.ImageScale, new JObject()
        {
            ["image"] = guideImage.Path,
            ["width"] = genInfo.Width,
            ["height"] = genInfo.Height,
            ["upscale_method"] = "lanczos",
            ["crop"] = "disabled"
        });
        g.CurrentMedia = g.CurrentMedia.WithPath([scaleNode, 0]);
    }

    private static void ApplyPostIfNeeded(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        ApplyLtxv2RootFirstFrameGuideIfNeeded(genInfo);
        ApplyLtxv2RootLastFrameGuideIfNeeded(genInfo);
        ApplyLatentDimensionsIfNeeded(genInfo);
    }

    private static void ApplyLtxv2RootFirstFrameGuideIfNeeded(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator g = genInfo?.Generator;
        if (g is null
            || !ShouldRewriteRootLtxv2GuideChain(genInfo)
            || g.CurrentMedia is null
            || genInfo.PosCond is null
            || genInfo.NegCond is null
            || genInfo.Vae is null
            || !TryResolveCurrentGuideChain(g, out string firstGuideNodeId, out JObject firstGuideNode, out string lastGuideNodeId, out JObject lastGuideNode)
            || !TryGetInputPath(firstGuideNode, "image", out JArray firstGuideImagePath)
            || !TryGetInputPath(firstGuideNode, "latent", out JArray firstGuideLatentPath))
        {
            return;
        }

        double guideStrength = GetGuideStrength(g);
        firstGuideNode["class_type"] = NodeTypes.LTXVAddGuide;
        firstGuideNode["inputs"] = new JObject()
        {
            ["positive"] = ClonePath(genInfo.PosCond),
            ["negative"] = ClonePath(genInfo.NegCond),
            ["vae"] = ClonePath(genInfo.Vae.Path),
            ["latent"] = ClonePath(firstGuideLatentPath),
            ["image"] = ClonePath(firstGuideImagePath),
            ["frame_idx"] = FirstFrameGuideFrameIndex,
            ["strength"] = guideStrength
        };

        genInfo.PosCond = new JArray(firstGuideNodeId, 0);
        genInfo.NegCond = new JArray(firstGuideNodeId, 1);
        g.CurrentMedia = g.CurrentMedia.WithPath([firstGuideNodeId, 2], WGNodeData.DT_LATENT_VIDEO, genInfo.Model?.Compat);

        if (lastGuideNode is null || string.IsNullOrWhiteSpace(lastGuideNodeId) || !TryGetInputPath(lastGuideNode, "image", out JArray lastGuideImagePath))
        {
            return;
        }

        lastGuideNode["class_type"] = NodeTypes.LTXVAddGuide;
        lastGuideNode["inputs"] = new JObject()
        {
            ["positive"] = ClonePath(genInfo.PosCond),
            ["negative"] = ClonePath(genInfo.NegCond),
            ["vae"] = ClonePath(genInfo.Vae.Path),
            ["latent"] = new JArray(firstGuideNodeId, 2),
            ["image"] = ClonePath(lastGuideImagePath),
            ["frame_idx"] = LastFrameGuideFrameIndex,
            ["strength"] = guideStrength
        };

        genInfo.PosCond = new JArray(lastGuideNodeId, 0);
        genInfo.NegCond = new JArray(lastGuideNodeId, 1);
        g.CurrentMedia = g.CurrentMedia.WithPath([lastGuideNodeId, 2], WGNodeData.DT_LATENT_VIDEO, genInfo.Model?.Compat);
    }

    private static void ApplyLtxv2RootLastFrameGuideIfNeeded(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator g = genInfo?.Generator;
        if (g is null)
        {
            return;
        }
        int? stageId = GetStageIdForContext(genInfo.ContextID);

        string guideReference = GetNormalizedRootGuideReference(
            g,
            RootGuideLastFrameReferenceHelperKey,
            VideoStagesExtension.RootGuideLastFrameReference,
            "Root guide last frame reference");
        if (!ShouldApplyLtxv2RootLastFrameGuide(genInfo, guideReference)
            || g.CurrentMedia is null
            || genInfo.PosCond is null
            || genInfo.NegCond is null
            || genInfo.Vae is null)
        {
            return;
        }

        WGNodeData guideImage = ResolveRootLastFrameGuideImage(genInfo, guideReference);
        if (guideImage is null)
        {
            return;
        }
        if (!TryGetRootGuideTargetResolution(genInfo, out int width, out int height))
        {
            return;
        }

        string resizeNode = g.CreateNode("ResizeImageMaskNode", new JObject()
        {
            ["input"] = guideImage.Path,
            ["resize_type"] = "scale dimensions",
            ["resize_type.width"] = width,
            ["resize_type.height"] = height,
            ["resize_type.crop"] = "center",
            ["scale_method"] = "nearest-exact"
        });
        string preprocessNode = g.CreateNode(NodeTypes.LTXVPreprocess, new JObject()
        {
            ["image"] = new JArray(resizeNode, 0),
            ["img_compression"] = LtxvGuideImgCompression
        });
        string addedGuide = g.CreateNode("LTXVAddGuide", new JObject()
        {
            ["positive"] = genInfo.PosCond,
            ["negative"] = genInfo.NegCond,
            ["vae"] = genInfo.Vae.Path,
            ["latent"] = g.CurrentMedia.Path,
            ["image"] = new JArray(preprocessNode, 0),
            ["frame_idx"] = LastFrameGuideFrameIndex,
            ["strength"] = GetGuideStrength(g, stageId)
        });
        genInfo.PosCond = new JArray(addedGuide, 0);
        genInfo.NegCond = new JArray(addedGuide, 1);
        g.CurrentMedia = g.CurrentMedia.WithPath([addedGuide, 2], WGNodeData.DT_LATENT_VIDEO, genInfo.Model?.Compat);
    }

    private static void ApplyLatentDimensionsIfNeeded(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator g = genInfo?.Generator;
        if (g is null
            || genInfo.ContextID != T2IParamInput.SectionID_Video
            || !TryGetRootStageResolution(g.UserInput, out int width, out int height)
            || g.CurrentMedia is null)
        {
            return;
        }

        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
    }

    internal static void ApplyRootAudioMaskDimensionsAfterNativeVideo(WorkflowGenerator g)
    {
        EnsureRootFinalGuideCropBeforeSave(g);
        if (!TryGetConfiguredRootStageResolution(g, out int width, out int height))
        {
            return;
        }

        UpdateAllAudioMaskDimensions(g, width, height);
    }

    internal static void ApplyCurrentAudioMaskDimensions(WGNodeData media)
    {
        if (media?.Gen is not WorkflowGenerator g
            || !media.Width.HasValue
            || !media.Height.HasValue
            || media.Path is not { Count: 2 } mediaPath
            || !g.Workflow.TryGetValue($"{mediaPath[0]}", out JToken concatToken)
            || concatToken is not JObject concatNode
            || $"{concatNode["class_type"]}" != NodeTypes.LTXVConcatAVLatent
            || concatNode["inputs"] is not JObject concatInputs
            || concatInputs["audio_latent"] is not JArray audioLatentPath
            || !TryGetSolidMaskInputsForAudioLatentPath(g, audioLatentPath, out JObject solidMaskInputs))
        {
            return;
        }

        solidMaskInputs["width"] = media.Width.Value;
        solidMaskInputs["height"] = media.Height.Value;
    }

    private static void EnsureRootFinalGuideCropBeforeSave(WorkflowGenerator g)
    {
        if (g?.CurrentMedia?.DataType != WGNodeData.DT_VIDEO
            || HasConfiguredStages(g)
            || g.CurrentMedia.Path is not { Count: 2 } currentMediaPath
            || !WorkflowUtils.TryResolveNearestUpstreamDecode(g.Workflow, currentMediaPath, out WorkflowNode decodeNode)
            || !TryGetDecodeLatentPath(decodeNode.Node, out JArray decodeLatentPath)
            || IsCropGuidesLatentPath(g, decodeLatentPath)
            || !TryResolveRootFinalGuideCropInputs(g, decodeLatentPath, out JArray positivePath, out JArray negativePath, out JArray videoLatentPath))
        {
            return;
        }

        string cropGuidesNode = g.CreateNode(NodeTypes.LTXVCropGuides, new JObject()
        {
            ["positive"] = ClonePath(positivePath),
            ["negative"] = ClonePath(negativePath),
            ["latent"] = ClonePath(videoLatentPath)
        });
        SetDecodeLatentPath(decodeNode.Node, new JArray(cropGuidesNode, 2));
    }

    private static bool IsDefaultRootGuideReference(string guideReference)
    {
        return string.IsNullOrWhiteSpace(guideReference)
            || string.Equals(guideReference, DefaultRootGuideReference, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExplicitRootLastFrameGuideReference(string guideReference)
    {
        return !IsDefaultRootGuideReference(guideReference);
    }

    private static bool ShouldApplyLtxv2RootLastFrameGuide(WorkflowGenerator.ImageToVideoGenInfo genInfo, string guideReference)
    {
        return genInfo?.VideoModel?.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatLtxv2.ID
            && (HasExplicitRootLastFrameGuideReference(guideReference)
                || (genInfo.ContextID != T2IParamInput.SectionID_Video && genInfo.VideoEndFrame is not null));
    }

    private static bool ShouldRewriteRootLtxv2GuideChain(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator g = genInfo?.Generator;
        return g is not null
            && genInfo.ContextID == T2IParamInput.SectionID_Video
            && genInfo.VideoModel?.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatLtxv2.ID
            && (HasConfiguredStages(g)
                || TryGetConfiguredRootStageResolution(g, out _, out _)
                || HasNonDefaultRootGuideSetting(g, VideoStagesExtension.RootGuideImageReference)
                || HasNonDefaultRootGuideSetting(g, VideoStagesExtension.RootGuideLastFrameReference));
    }

    private static bool HasConfiguredStages(WorkflowGenerator g)
    {
        if (g is null || !g.UserInput.Get(VideoStagesExtension.EnableVideoStages, false))
        {
            return false;
        }

        return new JsonParser(g).HasConfiguredStages();
    }

    private static bool HasNonDefaultRootGuideSetting(WorkflowGenerator g, T2IRegisteredParam<string> param)
    {
        if (g is null || param is null)
        {
            return false;
        }

        string compact = ImageReferenceSyntax.Compact(g.UserInput.Get(param, DefaultRootGuideReference));
        return !string.IsNullOrWhiteSpace(compact)
            && !string.Equals(compact, DefaultRootGuideReference, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveCurrentGuideChain(
        WorkflowGenerator g,
        out string firstGuideNodeId,
        out JObject firstGuideNode,
        out string lastGuideNodeId,
        out JObject lastGuideNode)
    {
        firstGuideNodeId = null;
        firstGuideNode = null;
        lastGuideNodeId = null;
        lastGuideNode = null;

        if (g?.CurrentMedia?.Path is not { Count: 2 } currentPath
            || !TryGetNode(g, $"{currentPath[0]}", out JObject currentNode))
        {
            return false;
        }

        string currentType = $"{currentNode["class_type"]}";
        if (currentType == NodeTypes.LTXVImgToVideoInplace)
        {
            firstGuideNodeId = $"{currentPath[0]}";
            firstGuideNode = currentNode;
            return true;
        }

        if (currentType != NodeTypes.LTXVAddGuide
            || !TryGetInputPath(currentNode, "latent", out JArray latentPath)
            || !TryGetNode(g, $"{latentPath[0]}", out JObject latentNode))
        {
            return false;
        }

        firstGuideNodeId = $"{latentPath[0]}";
        firstGuideNode = latentNode;
        lastGuideNodeId = $"{currentPath[0]}";
        lastGuideNode = currentNode;
        return true;
    }

    private static bool TryGetNode(WorkflowGenerator g, string nodeId, out JObject node)
    {
        node = null;
        return !string.IsNullOrWhiteSpace(nodeId)
            && g?.Workflow?.TryGetValue(nodeId, out JToken token) == true
            && (node = token as JObject) is not null;
    }

    private static bool TryGetInputPath(JObject node, string inputName, out JArray path)
    {
        path = null;
        return node?["inputs"] is JObject inputs
            && inputs[inputName] is JArray inputPath
            && inputPath.Count == 2
            && (path = inputPath) is not null;
    }

    private static bool TryGetDecodeLatentPath(JObject decodeNode, out JArray latentPath)
    {
        latentPath = null;
        return TryGetInputPath(decodeNode, "samples", out latentPath)
            || TryGetInputPath(decodeNode, "latent", out latentPath)
            || TryGetInputPath(decodeNode, "latents", out latentPath);
    }

    private static bool IsCropGuidesLatentPath(WorkflowGenerator g, JArray latentPath)
    {
        return latentPath is { Count: 2 }
            && $"{latentPath[1]}" == "2"
            && g?.Workflow?.TryGetValue($"{latentPath[0]}", out JToken cropToken) == true
            && cropToken is JObject cropNode
            && $"{cropNode["class_type"]}" == NodeTypes.LTXVCropGuides;
    }

    private static bool TryResolveRootFinalGuideCropInputs(
        WorkflowGenerator g,
        JArray decodeLatentPath,
        out JArray positivePath,
        out JArray negativePath,
        out JArray videoLatentPath)
    {
        positivePath = null;
        negativePath = null;
        videoLatentPath = null;
        if (g?.Workflow is null
            || decodeLatentPath is not { Count: 2 }
            || $"{decodeLatentPath[1]}" != "0"
            || !TryGetNode(g, $"{decodeLatentPath[0]}", out JObject separateNode)
            || $"{separateNode["class_type"]}" != NodeTypes.LTXVSeparateAVLatent
            || !TryGetInputPath(separateNode, "av_latent", out JArray avLatentPath)
            || !TryGetNode(g, $"{avLatentPath[0]}", out JObject samplerNode)
            || !TryGetInputPath(samplerNode, "positive", out JArray positiveRef)
            || !TryGetInputPath(samplerNode, "negative", out JArray negativeRef))
        {
            return false;
        }

        positivePath = ClonePath(positiveRef);
        negativePath = ClonePath(negativeRef);
        videoLatentPath = ClonePath(decodeLatentPath);
        return positivePath is not null && negativePath is not null && videoLatentPath is not null;
    }

    private static void SetDecodeLatentPath(JObject decodeNode, JArray latentPath)
    {
        if (decodeNode?["inputs"] is not JObject inputs || latentPath is not { Count: 2 })
        {
            return;
        }

        if (inputs["samples"] is not null)
        {
            inputs["samples"] = ClonePath(latentPath);
        }
        else if (inputs["latent"] is not null)
        {
            inputs["latent"] = ClonePath(latentPath);
        }
        else if (inputs["latents"] is not null)
        {
            inputs["latents"] = ClonePath(latentPath);
        }
        else
        {
            inputs["samples"] = ClonePath(latentPath);
        }
    }

    private static JArray ClonePath(JArray path)
    {
        return path is not { Count: 2 } ? null : new JArray(path[0], path[1]);
    }

    private static string GetNormalizedRootGuideReference(
        WorkflowGenerator g,
        string helperKey,
        T2IRegisteredParam<string> param,
        string settingName)
    {
        if (g.NodeHelpers.TryGetValue(helperKey, out string cached)
            && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        string normalized = NormalizeRootGuideReference(g, param, settingName);
        g.NodeHelpers[helperKey] = normalized;
        return normalized;
    }

    private static string NormalizeRootGuideReference(
        WorkflowGenerator g,
        T2IRegisteredParam<string> param,
        string settingName)
    {
        string rawValue = g.UserInput.Get(param, DefaultRootGuideReference);
        if (IsTextToVideoRootWorkflow(g))
        {
            string compactT2v = ImageReferenceSyntax.Compact(rawValue);
            if (!string.IsNullOrWhiteSpace(compactT2v)
                && !string.Equals(compactT2v, DefaultRootGuideReference, StringComparison.OrdinalIgnoreCase))
            {
                Logs.Warning($"VideoStages: {settingName} '{rawValue}' is invalid on a text-to-video workflow. Using '{DefaultRootGuideReference}' instead.");
            }
            return DefaultRootGuideReference;
        }

        string compact = ImageReferenceSyntax.Compact(rawValue);
        if (string.IsNullOrWhiteSpace(compact)
            || string.Equals(compact, DefaultRootGuideReference, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultRootGuideReference;
        }
        if (string.Equals(compact, "Base", StringComparison.OrdinalIgnoreCase))
        {
            return "Base";
        }
        if (string.Equals(compact, "Refiner", StringComparison.OrdinalIgnoreCase))
        {
            return "Refiner";
        }
        if (ImageReferenceSyntax.TryParseBase2EditStageIndex(compact, out int editStage))
        {
            return ImageReferenceSyntax.FormatBase2EditStageIndex(editStage);
        }

        Logs.Warning($"VideoStages: {settingName} '{rawValue}' is invalid. Using '{DefaultRootGuideReference}' instead.");
        return DefaultRootGuideReference;
    }

    private static StageRefStore.StageRef ResolveRootGuideReference(WorkflowGenerator g, string guideReference, string settingName)
    {
        StageRefStore store = new(g);
        if (guideReference == "Base")
        {
            return store.Base ?? throw new InvalidOperationException($"{settingName} 'Base' requested, but no base reference exists.");
        }
        if (guideReference == "Refiner")
        {
            return store.Refiner ?? throw new InvalidOperationException($"{settingName} 'Refiner' requested, but no refiner reference exists.");
        }
        if (ImageReferenceSyntax.TryParseBase2EditStageIndex(guideReference, out int editStage))
        {
            return Base2EditPublishedStageRefs.TryGetStageRef(g, editStage, out StageRefStore.StageRef editRef)
                ? editRef
                : throw new InvalidOperationException($"{settingName} '{guideReference}' requested, but Base2Edit stage {editStage} does not exist.");
        }

        throw new InvalidOperationException($"Unknown {settingName} value '{guideReference}'.");
    }

    private static WGNodeData ResolveRootGuideImage(WorkflowGenerator g, string guideReference, string settingName)
    {
        StageRefStore.StageRef stageRef = ResolveRootGuideReference(g, guideReference, settingName);
        WGNodeData guideImage = Base2EditPublishedStageRefs.ResolveToRawImage(stageRef);
        if (guideImage is null)
        {
            throw new InvalidOperationException($"{settingName} '{guideReference}' could not be resolved to an image.");
        }
        return guideImage;
    }

    private static WGNodeData ResolveRootLastFrameGuideImage(WorkflowGenerator.ImageToVideoGenInfo genInfo, string guideReference)
    {
        WorkflowGenerator g = genInfo?.Generator;
        if (g is null)
        {
            return null;
        }
        if (HasExplicitRootLastFrameGuideReference(guideReference))
        {
            return ResolveRootGuideImage(g, guideReference, "Root guide last frame reference");
        }
        if (genInfo.ContextID != T2IParamInput.SectionID_Video && genInfo.VideoEndFrame is not null)
        {
            return g.LoadImage(genInfo.VideoEndFrame, "${videoendframe}", false);
        }
        return null;
    }

    private static bool IsTextToVideoRootWorkflow(WorkflowGenerator g)
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel imageToVideoModel) && imageToVideoModel is not null)
        {
            return false;
        }

        return g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true;
    }

    private static bool TryGetRootStageResolution(T2IParamInput input, out int width, out int height)
    {
        width = 0;
        height = 0;
        return input.TryGet(VideoStagesExtension.RootStageWidth, out width)
            && input.TryGet(VideoStagesExtension.RootStageHeight, out height);
    }

    internal static bool TryGetConfiguredRootStageResolution(WorkflowGenerator g, out int width, out int height)
    {
        width = 0;
        height = 0;
        return g is not null
            && TryGetRootStageResolution(g.UserInput, out width, out height);
    }

    internal static double GetGuideStrength(WorkflowGenerator g, int? stageId = null)
    {
        double defaultStrength = GetDefaultGuideStrength(stageId);
        if (g?.UserInput is null
            || !g.UserInput.TryGet(VideoStagesExtension.LTXVImgToVideoInplaceStrength, out double strength))
        {
            return defaultStrength;
        }

        // Older hidden payloads can still carry the previous default of 0.8.
        if (Math.Abs(strength - LegacyDefaultGuideStrength) < 0.000001d)
        {
            return defaultStrength;
        }

        return strength;
    }

    private static double GetDefaultGuideStrength(int? stageId)
        => stageId.GetValueOrDefault() >= 1
            ? AdditionalStageGuideStrength
            : VideoStagesExtension.DefaultLTXVImgToVideoInplaceStrength;

    private static int? GetStageIdForContext(int contextId)
    {
        int stageId = contextId - VideoStagesExtension.SectionID_VideoStages - 1;
        return stageId >= 0 ? stageId : null;
    }

    private static bool TryGetRootGuideTargetResolution(WorkflowGenerator.ImageToVideoGenInfo genInfo, out int width, out int height)
    {
        WorkflowGenerator g = genInfo?.Generator;
        width = g?.CurrentMedia?.Width ?? 0;
        height = g?.CurrentMedia?.Height ?? 0;
        if (width > 0 && height > 0)
        {
            return true;
        }
        width = genInfo?.Width?.Value<int?>() ?? 0;
        height = genInfo?.Height?.Value<int?>() ?? 0;
        if (width > 0 && height > 0)
        {
            return true;
        }
        if (TryGetConfiguredRootStageResolution(g, out width, out height))
        {
            return true;
        }

        if (g is null)
        {
            return false;
        }

        width = g.UserInput.GetImageWidth();
        height = g.UserInput.GetImageHeight();
        return width > 0 && height > 0;
    }

    private static bool TryUpdateExistingScaleNode(WorkflowGenerator g, JArray imagePath = null, int? width = null, int? height = null, string crop = null)
    {
        if (g.CurrentMedia?.Path is not { Count: 2 } currentPath
            || !g.Workflow.TryGetValue($"{currentPath[0]}", out JToken currentToken)
            || currentToken is not JObject currentNode
            || $"{currentNode["class_type"]}" != NodeTypes.ImageScale
            || currentNode["inputs"] is not JObject inputs)
        {
            return false;
        }

        if (imagePath is not null)
        {
            inputs["image"] = imagePath;
        }
        if (width.HasValue)
        {
            inputs["width"] = width.Value;
        }
        if (height.HasValue)
        {
            inputs["height"] = height.Value;
        }
        if (!string.IsNullOrWhiteSpace(crop))
        {
            inputs["crop"] = crop;
        }
        if (inputs["upscale_method"] is null)
        {
            inputs["upscale_method"] = "lanczos";
        }
        return true;
    }

    private static void UpdateAllAudioMaskDimensions(WorkflowGenerator g, int width, int height)
    {
        foreach (WorkflowNode setMaskNode in WorkflowUtils.NodesOfType(g.Workflow, NodeTypes.SetLatentNoiseMask))
        {
            if (!IsAudioNoiseMaskNode(g, setMaskNode.Node)
                || !TryGetSolidMaskInputsForSetMaskNode(g, setMaskNode.Node, out JObject solidMaskInputs))
            {
                continue;
            }

            solidMaskInputs["width"] = width;
            solidMaskInputs["height"] = height;
        }
    }

    private static bool IsAudioNoiseMaskNode(WorkflowGenerator g, JObject setMaskNode)
    {
        if (setMaskNode["inputs"] is not JObject inputs
            || inputs["samples"] is not JArray samplesPath
            || samplesPath.Count != 2
            || !g.Workflow.TryGetValue($"{samplesPath[0]}", out JToken samplesToken)
            || samplesToken is not JObject samplesNode)
        {
            return false;
        }

        string classType = $"{samplesNode["class_type"]}";
        return classType == "LTXVAudioVAEEncode" || classType == "VAEEncodeAudio";
    }

    private static bool TryGetSolidMaskInputsForAudioLatentPath(WorkflowGenerator g, JArray audioLatentPath, out JObject solidMaskInputs)
    {
        solidMaskInputs = null;
        if (audioLatentPath is not { Count: 2 }
            || !g.Workflow.TryGetValue($"{audioLatentPath[0]}", out JToken setMaskToken)
            || setMaskToken is not JObject setMaskNode
            || $"{setMaskNode["class_type"]}" != NodeTypes.SetLatentNoiseMask)
        {
            return false;
        }

        return TryGetSolidMaskInputsForSetMaskNode(g, setMaskNode, out solidMaskInputs);
    }

    private static bool TryGetSolidMaskInputsForSetMaskNode(WorkflowGenerator g, JObject setMaskNode, out JObject solidMaskInputs)
    {
        solidMaskInputs = null;
        if (setMaskNode["inputs"] is not JObject setMaskInputs
            || setMaskInputs["mask"] is not JArray solidMaskPath
            || solidMaskPath.Count != 2
            || !g.Workflow.TryGetValue($"{solidMaskPath[0]}", out JToken solidMaskToken)
            || solidMaskToken is not JObject solidMaskNode
            || $"{solidMaskNode["class_type"]}" != NodeTypes.SolidMask
            || solidMaskNode["inputs"] is not JObject inputs)
        {
            return false;
        }

        solidMaskInputs = inputs;
        return true;
    }
}
