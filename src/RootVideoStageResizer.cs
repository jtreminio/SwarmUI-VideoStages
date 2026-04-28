using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal sealed class RootVideoStageResizer(WorkflowGenerator g)
{
    private static readonly Action<WorkflowGenerator.ImageToVideoGenInfo> PreHandler = ApplyIfNeededForHandler;
    private static readonly Action<WorkflowGenerator.ImageToVideoGenInfo> PostHandler = ApplyLatentDimensionsIfNeededForHandler;

    public static void EnsureRegistered()
    {
        EnsureHandlerInList(WorkflowGenerator.AltImageToVideoPreHandlers, PreHandler);
        EnsureHandlerInList(WorkflowGenerator.AltImageToVideoPostHandlers, PostHandler);
    }

    private static void EnsureHandlerInList(
        List<Action<WorkflowGenerator.ImageToVideoGenInfo>> list,
        Action<WorkflowGenerator.ImageToVideoGenInfo> handler)
    {
        if (!list.Contains(handler))
        {
            list.Add(handler);
        }
    }

    private static void ApplyIfNeededForHandler(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!TryGetVideoContextRootSize(genInfo, out WorkflowGenerator handlerGenerator, out int width, out int height))
        {
            return;
        }

        new RootVideoStageResizer(handlerGenerator).ApplyIfNeeded(genInfo, width, height);
    }

    private static void ApplyLatentDimensionsIfNeededForHandler(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!TryGetVideoContextRootSize(genInfo, out WorkflowGenerator handlerGenerator, out int width, out int height)
            || handlerGenerator.CurrentMedia is null)
        {
            return;
        }

        new RootVideoStageResizer(handlerGenerator).ApplyLatentDimensions(width, height);
    }

    private void ApplyIfNeeded(WorkflowGenerator.ImageToVideoGenInfo genInfo, int width, int height)
    {
        genInfo.Width = width;
        genInfo.Height = height;
        ApplyCurrentMediaResolution(width, height);
    }

    private void ApplyLatentDimensions(int width, int height)
    {
        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
    }

    private static bool TryGetVideoContextRootSize(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        out WorkflowGenerator g,
        out int width,
        out int height)
    {
        g = genInfo?.Generator;
        width = 0;
        height = 0;
        if (g is null || genInfo.ContextID != T2IParamInput.SectionID_Video)
        {
            return false;
        }

        return new RootVideoStageResizer(g).TryGetRootStageResolution(out width, out height);
    }

    internal void ApplyConfiguredRootStageResolutionToCurrentMedia()
    {
        if (!TryGetConfiguredRootStageResolution(out int width, out int height))
        {
            return;
        }

        if (CurrentMediaFeedsSwarmOrComfySaveImage())
        {
            g.CurrentMedia.Width = width;
            g.CurrentMedia.Height = height;
            return;
        }

        ApplyCurrentMediaResolution(width, height);
    }

    private bool CurrentMediaFeedsSwarmOrComfySaveImage()
    {
        if (!new RootVideoStageTakeover(g).ShouldTakeOverRootStage())
        {
            return false;
        }

        if (g.CurrentMedia?.Path is not JArray mediaPath || mediaPath.Count != 2)
        {
            return false;
        }

        foreach (WorkflowInputConnection connection in WorkflowUtils.FindInputConnections(g.Workflow, mediaPath))
        {
            if (!g.Workflow.TryGetValue(connection.NodeId, out JToken nodeToken) || nodeToken is not JObject node)
            {
                continue;
            }

            string classType = $"{node["class_type"]}";
            if (classType == NodeTypes.SwarmSaveImageWS || classType == "SaveImage")
            {
                return true;
            }
        }

        return false;
    }

    private bool HasNativeRootVideoModel()
    {
        return g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel imageToVideoModel)
            && imageToVideoModel is not null;
    }

    internal bool TryGetRootStageResolution(out int width, out int height)
    {
        width = 0;
        height = 0;
        if (g is null)
        {
            return false;
        }

        if (TryGetRegisteredRootStageResolution(out width, out height))
        {
            return true;
        }

        JsonParser parser = new(g);
        JsonParser.VideoStagesSpec config = parser.ParseConfig();
        if (TryPositiveDimensionPair(config.Width, config.Height, out width, out height))
        {
            return true;
        }

        foreach (JsonParser.ClipSpec clip in config.Clips)
        {
            if (clip.Skipped)
            {
                continue;
            }

            if (TryPositiveDimensionPair(clip.Width, clip.Height, out width, out height))
            {
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryPositiveDimensionPair(int? w, int? h, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!w.HasValue || !h.HasValue || w.Value <= 0 || h.Value <= 0)
        {
            return false;
        }

        width = w.Value;
        height = h.Value;
        return true;
    }

    private bool TryGetRegisteredRootStageResolution(out int width, out int height)
    {
        width = 0;
        height = 0;
        return g.UserInput.TryGet(VideoStagesExtension.RootWidth, out width)
            && g.UserInput.TryGet(VideoStagesExtension.RootHeight, out height)
            && width >= Constants.RootDimensionMin
            && height >= Constants.RootDimensionMin;
    }

    internal bool TryGetConfiguredRootStageResolution(out int width, out int height)
    {
        width = 0;
        height = 0;
        return g is not null
            && HasNativeRootVideoModel()
            && TryGetRootStageResolution(out width, out height);
    }

    private void ApplyCurrentMediaResolution(int width, int height)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }

        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
        if (TryUpdateExistingImageScaleNode(width, height))
        {
            return;
        }

        string scaleNode = g.CreateNode(
            NodeTypes.ImageScale,
            StageGuideMediaHelper.BuildCenterLanczosImageScaleInputs(g.CurrentMedia.Path, width, height));
        g.CurrentMedia = g.CurrentMedia.WithPath([scaleNode, 0]);
    }

    private bool TryUpdateExistingImageScaleNode(int width, int height)
    {
        if (g.CurrentMedia?.Path is not { Count: 2 } currentPath
            || !g.Workflow.TryGetValue($"{currentPath[0]}", out JToken currentToken)
            || currentToken is not JObject currentNode
            || $"{currentNode["class_type"]}" != NodeTypes.ImageScale
            || currentNode["inputs"] is not JObject inputs)
        {
            return false;
        }

        inputs["width"] = width;
        inputs["height"] = height;
        inputs["crop"] = "center";
        if (inputs["upscale_method"] is null)
        {
            inputs["upscale_method"] = "lanczos";
        }

        return true;
    }
}
