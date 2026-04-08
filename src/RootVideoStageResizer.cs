using System;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal static class RootVideoStageResizer
{
    private const string DefaultRootGuideReference = "Default";
    private static readonly Action<WorkflowGenerator.ImageToVideoGenInfo> PreHandler = ApplyIfNeeded;
    private static readonly Action<WorkflowGenerator.ImageToVideoGenInfo> PostHandler = ApplyLatentDimensionsIfNeeded;

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
            || genInfo.ContextID != T2IParamInput.SectionID_Video
            || !HasConfiguredStages(g))
        {
            return;
        }

        ApplyRootGuideReferenceIfNeeded(genInfo);
        if (!TryGetRootStageResolution(g.UserInput, out int width, out int height))
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

    private static void ApplyRootGuideReferenceIfNeeded(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator g = genInfo?.Generator;
        if (g is null)
        {
            return;
        }

        string guideReference = NormalizeRootGuideImageReference(g);
        if (guideReference == DefaultRootGuideReference)
        {
            return;
        }

        StageRefStore.StageRef stageRef = ResolveRootGuideReference(g, guideReference);
        WGNodeData guideImage = Base2EditPublishedStageRefs.ResolveToRawImage(stageRef);
        if (guideImage is null)
        {
            throw new InvalidOperationException($"Root Guide Image Reference '{guideReference}' could not be resolved to an image.");
        }

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

    private static void ApplyLatentDimensionsIfNeeded(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator g = genInfo?.Generator;
        if (g is null
            || genInfo.ContextID != T2IParamInput.SectionID_Video
            || !HasConfiguredStages(g)
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

    private static bool HasConfiguredStages(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(VideoStagesExtension.EnableVideoStages, false))
        {
            return false;
        }

        if (!g.UserInput.TryGet(VideoStagesExtension.VideoStagesJson, out string json)
            || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        return json.Trim() != "[]";
    }

    private static string NormalizeRootGuideImageReference(WorkflowGenerator g)
    {
        string rawValue = g.UserInput.Get(VideoStagesExtension.RootGuideImageReference, DefaultRootGuideReference);
        if (IsTextToVideoRootWorkflow(g))
        {
            string compactT2v = ImageReferenceSyntax.Compact(rawValue);
            if (!string.IsNullOrWhiteSpace(compactT2v)
                && !string.Equals(compactT2v, DefaultRootGuideReference, StringComparison.OrdinalIgnoreCase))
            {
                Logs.Warning($"VideoStages: Root guide reference '{rawValue}' is invalid on a text-to-video workflow. Using '{DefaultRootGuideReference}' instead.");
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

        Logs.Warning($"VideoStages: Root guide reference '{rawValue}' is invalid. Using '{DefaultRootGuideReference}' instead.");
        return DefaultRootGuideReference;
    }

    private static StageRefStore.StageRef ResolveRootGuideReference(WorkflowGenerator g, string guideReference)
    {
        StageRefStore store = new(g);
        if (guideReference == "Base")
        {
            return store.Base ?? throw new InvalidOperationException("Root Guide Image Reference 'Base' requested, but no base reference exists.");
        }
        if (guideReference == "Refiner")
        {
            return store.Refiner ?? throw new InvalidOperationException("Root Guide Image Reference 'Refiner' requested, but no refiner reference exists.");
        }
        if (ImageReferenceSyntax.TryParseBase2EditStageIndex(guideReference, out int editStage))
        {
            return Base2EditPublishedStageRefs.TryGetStageRef(g, editStage, out StageRefStore.StageRef editRef)
                ? editRef
                : throw new InvalidOperationException($"Root Guide Image Reference '{guideReference}' requested, but Base2Edit stage {editStage} does not exist.");
        }

        throw new InvalidOperationException($"Unknown Root Guide Image Reference value '{guideReference}'.");
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
            && HasConfiguredStages(g)
            && TryGetRootStageResolution(g.UserInput, out width, out height);
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
        foreach (var setMaskNode in WorkflowUtils.NodesOfType(g.Workflow, NodeTypes.SetLatentNoiseMask))
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
