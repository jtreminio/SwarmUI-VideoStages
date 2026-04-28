using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal sealed class RootVideoStageResizer(WorkflowGenerator g, RootVideoStageTakeover takeover)
{
    private readonly record struct Resolution(int Width, int Height);

    public static void RegisterHandlers()
    {
        WorkflowGenerator.AltImageToVideoPreHandlers.Add(ApplyRootResolutionBeforeImageToVideo);
        WorkflowGenerator.AltImageToVideoPostHandlers.Add(ApplyRootLatentResolutionAfterImageToVideo);
    }

    private static void ApplyRootResolutionBeforeImageToVideo(
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!TryGetVideoContextResizerWithRootSize(genInfo, out RootVideoStageResizer resizer, out Resolution resolution))
        {
            return;
        }

        resizer.ApplyRootResolutionToImageToVideoInput(genInfo, resolution);
    }

    private static void ApplyRootLatentResolutionAfterImageToVideo(
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!TryGetVideoContextResizerWithRootSize(genInfo, out RootVideoStageResizer resizer, out Resolution resolution))
        {
            return;
        }

        resizer.ApplyRootResolutionToCurrentMediaLatent(resolution);
    }

    private static bool TryGetVideoContextResizerWithRootSize(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        out RootVideoStageResizer resizer,
        out Resolution resolution)
    {
        resizer = null;
        resolution = default;

        if (genInfo.ContextID != T2IParamInput.SectionID_Video)
        {
            return false;
        }

        WorkflowGenerator g = genInfo.Generator;
        resizer = new RootVideoStageResizer(g, new RootVideoStageTakeover(g));
        return resizer.TryGetRootStageResolution(out resolution);
    }

    private void ApplyRootResolutionToImageToVideoInput(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        Resolution resolution)
    {
        genInfo.Width = resolution.Width;
        genInfo.Height = resolution.Height;
        ApplyCurrentMediaResolution(resolution);
    }

    private void ApplyRootResolutionToCurrentMediaLatent(Resolution resolution)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }

        g.CurrentMedia.Width = resolution.Width;
        g.CurrentMedia.Height = resolution.Height;
    }

    internal void ApplyConfiguredRootStageResolutionToCurrentMedia()
    {
        if (!TryGetConfiguredRootStageResolution(out Resolution resolution))
        {
            return;
        }

        if (CurrentMediaFeedsSaveImage())
        {
            g.CurrentMedia.Width = resolution.Width;
            g.CurrentMedia.Height = resolution.Height;
            return;
        }

        ApplyCurrentMediaResolution(resolution);
    }

    private bool CurrentMediaFeedsSaveImage()
    {
        if (!takeover.ShouldTakeOverRootStage())
        {
            return false;
        }

        if (g.CurrentMedia?.Path is not { Count: 2 } mediaPath)
        {
            return false;
        }

        foreach (WorkflowInputConnection connection in WorkflowUtils.FindInputConnections(g.Workflow, mediaPath))
        {
            if (!g.Workflow.TryGetValue(connection.NodeId, out JToken nodeToken) || nodeToken is not JObject node)
            {
                continue;
            }

            if (StringUtils.NodeTypeMatches(node, NodeTypes.SwarmSaveImageWS)
                || StringUtils.NodeTypeMatches(node, NodeTypes.SaveImage))
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
        if (!TryGetRootStageResolution(out Resolution resolution))
        {
            width = 0;
            height = 0;
            return false;
        }

        width = resolution.Width;
        height = resolution.Height;
        return true;
    }

    private bool TryGetRootStageResolution(out Resolution resolution)
    {
        if (TryGetRegisteredRootStageResolution(out resolution))
        {
            return true;
        }

        JsonParser parser = new(g);
        JsonParser.VideoStagesSpec config = parser.ParseConfig();
        if (TryPositiveDimensionPair(config.Width, config.Height, out resolution))
        {
            return true;
        }

        foreach (JsonParser.ClipSpec clip in config.Clips)
        {
            if (!clip.Skipped)
            {
                return TryPositiveDimensionPair(clip.Width, clip.Height, out resolution);
            }
        }

        resolution = default;
        return false;
    }

    private static bool TryPositiveDimensionPair(int? w, int? h, out Resolution resolution)
    {
        resolution = default;
        if (!w.HasValue || !h.HasValue || w.Value <= 0 || h.Value <= 0)
        {
            return false;
        }

        resolution = new Resolution(w.Value, h.Value);
        return true;
    }

    private bool TryGetRegisteredRootStageResolution(out Resolution resolution)
    {
        resolution = default;
        if (!g.UserInput.TryGet(VideoStagesExtension.RootWidth, out int width)
            || !g.UserInput.TryGet(VideoStagesExtension.RootHeight, out int height)
            || width < Constants.RootDimensionMin
            || height < Constants.RootDimensionMin)
        {
            return false;
        }

        resolution = new Resolution(width, height);
        return true;
    }

    internal bool TryGetConfiguredRootStageResolution(out int width, out int height)
    {
        if (!TryGetConfiguredRootStageResolution(out Resolution resolution))
        {
            width = 0;
            height = 0;
            return false;
        }

        width = resolution.Width;
        height = resolution.Height;
        return true;
    }

    private bool TryGetConfiguredRootStageResolution(out Resolution resolution)
    {
        resolution = default;
        return g is not null
            && HasNativeRootVideoModel()
            && TryGetRootStageResolution(out resolution);
    }

    private void ApplyCurrentMediaResolution(Resolution resolution)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }

        g.CurrentMedia.Width = resolution.Width;
        g.CurrentMedia.Height = resolution.Height;
        if (TryUpdateExistingImageScaleNode(resolution))
        {
            return;
        }

        JObject scaleInputs = StageGuideMediaHelper.BuildCenterLanczosImageScaleInputs(
            g.CurrentMedia.Path,
            resolution.Width,
            resolution.Height);

        string scaleNode = g.CreateNode(
            NodeTypes.ImageScale,
            scaleInputs);
        g.CurrentMedia = g.CurrentMedia.WithPath([scaleNode, 0]);
    }

    private bool TryUpdateExistingImageScaleNode(Resolution resolution)
    {
        if (g.CurrentMedia?.Path is not { Count: 2 } currentPath
            || !g.Workflow.TryGetValue($"{currentPath[0]}", out JToken currentToken)
            || currentToken is not JObject currentNode
            || !StringUtils.NodeTypeMatches(currentNode, NodeTypes.ImageScale)
            || currentNode["inputs"] is not JObject inputs)
        {
            return false;
        }

        inputs["width"] = resolution.Width;
        inputs["height"] = resolution.Height;
        inputs["crop"] = "center";
        if (inputs["upscale_method"] is null)
        {
            inputs["upscale_method"] = "lanczos";
        }

        return true;
    }
}
