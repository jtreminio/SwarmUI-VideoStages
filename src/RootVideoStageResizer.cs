using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal sealed class RootVideoStageResizer(
    WorkflowGenerator g,
    RootVideoStageTakeover takeover,
    JsonParser jsonParser)
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
        if (!TryGetVideoContextResizerWithRootSize(
            genInfo, out RootVideoStageResizer resizer, out Resolution resolution))
        {
            return;
        }

        resizer.ApplyRootResolutionToImageToVideoInput(genInfo, resolution);
    }

    private static void ApplyRootLatentResolutionAfterImageToVideo(
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!TryGetVideoContextResizerWithRootSize(
            genInfo, out RootVideoStageResizer resizer, out Resolution resolution))
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
        resizer = new Runner(g).RootVideoStageResizer;
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

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput output = bridge.ResolvePath(mediaPath);
        if (output is null)
        {
            return false;
        }

        foreach (ComfyNode consumer in bridge.Graph.FindDownstream(output))
        {
            if (consumer is SwarmSaveImageWSNode or SaveImageNode)
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

        JsonParser.VideoStagesSpec config = jsonParser.ParseConfig();
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

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (TryUpdateExistingImageScaleNode(bridge, resolution))
        {
            return;
        }

        if (g.CurrentMedia.Path is not JArray currentMediaPath
            || bridge.ResolvePath(currentMediaPath) is not INodeOutput sourceOutput)
        {
            return;
        }

        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode());
        scale.Image.ConnectToUntyped(sourceOutput);
        scale.UpscaleMethod.Set("lanczos");
        scale.Width.Set(resolution.Width);
        scale.Height.Set(resolution.Height);
        scale.Crop.Set("center");
        bridge.SyncNode(scale);
        BridgeSync.SyncLastId(g);

        g.CurrentMedia = g.CurrentMedia.WithPath([scale.Id, 0]);
    }

    private bool TryUpdateExistingImageScaleNode(WorkflowBridge bridge, Resolution resolution)
    {
        if (g.CurrentMedia?.Path is not { Count: 2 } currentPath)
        {
            return false;
        }
        if (bridge.Graph.GetNode($"{currentPath[0]}") is not ImageScaleNode scale)
        {
            return false;
        }

        scale.Width.Set(resolution.Width);
        scale.Height.Set(resolution.Height);
        scale.Crop.Set("center");
        if (!scale.UpscaleMethod.HasValue)
        {
            scale.UpscaleMethod.Set("lanczos");
        }
        bridge.SyncNode(scale);
        return true;
    }

}
