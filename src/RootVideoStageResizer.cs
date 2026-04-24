using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

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

    internal static void ApplyConfiguredRootStageResolutionToCurrentMedia(WorkflowGenerator g)
    {
        if (!TryGetConfiguredRootStageResolution(g, out int width, out int height))
        {
            return;
        }

        // Takeover skips CreateImageToVideo; do not insert ImageScale if this tensor already feeds SwarmSaveImageWS/SaveImage.
        if (RootVideoStageTakeover.ShouldTakeOverRootStage(g)
            && g.CurrentMedia?.Path is JArray mediaPath
            && mediaPath.Count == 2
            && WorkflowUtils.FindInputConnections(g.Workflow, mediaPath)
                .Any(connection =>
                    g.Workflow.TryGetValue(connection.NodeId, out JToken nodeToken)
                    && nodeToken is JObject node
                    && ($"{node["class_type"]}" == NodeTypes.SwarmSaveImageWS
                        || $"{node["class_type"]}" == "SaveImage")))
        {
            g.CurrentMedia.Width = width;
            g.CurrentMedia.Height = height;
            return;
        }

        ApplyCurrentMediaResolution(g, width, height);
    }

    private static bool HasNativeRootVideoModel(WorkflowGenerator g)
    {
        return g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel imageToVideoModel)
            && imageToVideoModel is not null;
    }

    /// <summary>Root stage width/height from registered input or parsed JSON (top-level, else first non-skipped clip).</summary>
    internal static bool TryGetRootStageResolution(WorkflowGenerator g, out int width, out int height)
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
}
