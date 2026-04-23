using System;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal static class RootVideoStageResizer
{
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
            || !TryGetRootStageResolution(g, out int width, out int height))
        {
            return;
        }

        genInfo.Width = width;
        genInfo.Height = height;
        ApplyCurrentMediaResolution(g, width, height);
    }

    private static void ApplyLatentDimensionsIfNeeded(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator g = genInfo?.Generator;
        if (g is null
            || genInfo.ContextID != T2IParamInput.SectionID_Video
            || !TryGetRootStageResolution(g, out int width, out int height)
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

    internal static void ApplyConfiguredRootStageResolutionToCurrentMedia(WorkflowGenerator g)
    {
        if (!TryGetConfiguredRootStageResolution(g, out int width, out int height))
        {
            return;
        }

        ApplyCurrentMediaResolution(g, width, height);
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

    private static bool HasNativeRootVideoModel(WorkflowGenerator g)
    {
        return g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel imageToVideoModel)
            && imageToVideoModel is not null;
    }

    /// <summary>
    /// Returns the resolution to use for the root video stage. The current
    /// editor stores Width / Height at the top level; older clip-scoped JSON
    /// is still accepted as a fallback for backward compatibility.
    /// </summary>
    private static bool TryGetRootStageResolution(WorkflowGenerator g, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (g is null)
        {
            return false;
        }

        if (TryGetRegisteredRootStageResolution(g, out width, out height))
        {
            return true;
        }

        JsonParser parser = new(g);
        JsonParser.VideoStagesSpec config = parser.ParseConfig();
        if (config.Width.HasValue && config.Height.HasValue && config.Width.Value > 0 && config.Height.Value > 0)
        {
            width = config.Width.Value;
            height = config.Height.Value;
            return true;
        }
        foreach (JsonParser.ClipSpec clip in config.Clips)
        {
            if (clip.Skipped)
            {
                continue;
            }
            if (clip.Width.HasValue && clip.Height.HasValue && clip.Width.Value > 0 && clip.Height.Value > 0)
            {
                width = clip.Width.Value;
                height = clip.Height.Value;
                return true;
            }
            return false;
        }
        return false;
    }

    private static bool TryGetRegisteredRootStageResolution(WorkflowGenerator g, out int width, out int height)
    {
        width = 0;
        height = 0;
        return g.UserInput.TryGet(VideoStagesExtension.RootWidth, out width)
            && g.UserInput.TryGet(VideoStagesExtension.RootHeight, out height)
            && width >= VideoStagesExtension.RootDimensionMin
            && height >= VideoStagesExtension.RootDimensionMin;
    }

    internal static bool TryGetConfiguredRootStageResolution(WorkflowGenerator g, out int width, out int height)
    {
        width = 0;
        height = 0;
        return g is not null
            && HasNativeRootVideoModel(g)
            && TryGetRootStageResolution(g, out width, out height);
    }

    private static void ApplyCurrentMediaResolution(WorkflowGenerator g, int width, int height)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }

        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
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
