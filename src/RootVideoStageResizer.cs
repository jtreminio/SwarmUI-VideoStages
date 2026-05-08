using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal sealed class RootVideoStageResizer(
    WorkflowGenerator g,
    RootVideoStageHandoff handoff,
    JsonParser jsonParser)
{
    public static void RegisterHandlers()
    {
        WorkflowGenerator.AltImageToVideoPreHandlers.Add(ApplyRootResolutionBeforeImageToVideo);
        WorkflowGenerator.AltImageToVideoPostHandlers.Add(ApplyRootLatentResolutionAfterImageToVideo);
    }

    private static void ApplyRootResolutionBeforeImageToVideo(
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!TryGetVideoContextResizerWithRootSize(
            genInfo, out RootVideoStageResizer resizer, out int width, out int height))
        {
            return;
        }

        genInfo.Width = width;
        genInfo.Height = height;
        resizer.ApplyCurrentMediaResolution(width, height);
    }

    private static void ApplyRootLatentResolutionAfterImageToVideo(
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!TryGetVideoContextResizerWithRootSize(
            genInfo, out RootVideoStageResizer resizer, out int width, out int height))
        {
            return;
        }

        resizer.SetCurrentMediaDimensions(width, height);
    }

    private static bool TryGetVideoContextResizerWithRootSize(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        out RootVideoStageResizer resizer,
        out int width,
        out int height)
    {
        resizer = null;
        width = 0;
        height = 0;

        if (genInfo.ContextID != T2IParamInput.SectionID_Video)
        {
            return false;
        }

        resizer = Runner.GetRootVideoStageResizer(genInfo.Generator);
        return resizer.TryGetRootStageResolution(out width, out height);
    }

    internal void ApplyConfiguredRootStageResolutionToCurrentMedia()
    {
        if (!TryGetRootStageResolution(out int width, out int height))
        {
            return;
        }
        if (CurrentMediaFeedsSaveImage())
        {
            SetCurrentMediaDimensions(width, height);
            return;
        }

        ApplyCurrentMediaResolution(width, height);
    }

    internal bool TryGetRootStageResolution(out int width, out int height)
    {
        if (TryGetRegisteredRootStageResolution(out width, out height))
        {
            return true;
        }

        JsonParser.VideoStagesSpec config = jsonParser.ParseConfig();
        if (TryPositiveDimensionPair(config.Width, config.Height, out width, out height))
        {
            return true;
        }

        foreach (JsonParser.ClipSpec clip in config.Clips)
        {
            if (!clip.Skipped)
            {
                return TryPositiveDimensionPair(clip.Width, clip.Height, out width, out height);
            }
        }

        width = 0;
        height = 0;
        return false;
    }

    internal bool TryGetConfiguredRootStageResolution(out int width, out int height)
    {
        return TryGetRootStageResolution(out width, out height);
    }

    private bool CurrentMediaFeedsSaveImage()
    {
        if (!handoff.ShouldHandoffRootStage()
            || g.CurrentMedia?.Path is not { Count: 2 } mediaPath)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.ResolvePath(mediaPath) is not INodeOutput output)
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

    private static bool TryPositiveDimensionPair(int? w, int? h, out int width, out int height)
    {
        if (w is > 0 && h is > 0)
        {
            width = w.Value;
            height = h.Value;
            return true;
        }
        width = 0;
        height = 0;
        return false;
    }

    private bool TryGetRegisteredRootStageResolution(out int width, out int height)
    {
        if (g.UserInput.TryGet(VideoStagesExtension.RootWidth, out width)
            && g.UserInput.TryGet(VideoStagesExtension.RootHeight, out height)
            && width >= Constants.RootDimensionMin
            && height >= Constants.RootDimensionMin)
        {
            return true;
        }
        width = 0;
        height = 0;
        return false;
    }

    private void SetCurrentMediaDimensions(int width, int height)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }
        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
    }

    private void ApplyCurrentMediaResolution(int width, int height)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }
        SetCurrentMediaDimensions(width, height);

        if (g.CurrentMedia.Path is not JArray path || path.Count != 2)
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ImageScaleNode scale = bridge.Graph.GetNode<ImageScaleNode>($"{path[0]}");
        if (scale is null)
        {
            if (bridge.ResolvePath(path) is not INodeOutput sourceOutput)
            {
                return;
            }
            scale = bridge.AddNode(new ImageScaleNode());
            scale.Image.ConnectToUntyped(sourceOutput);
            BridgeSync.SyncLastId(g);
            g.CurrentMedia = g.CurrentMedia.WithPath([scale.Id, 0]);
        }

        scale.With(
            Width: width,
            Height: height,
            Crop: "center");
        if (!scale.UpscaleMethod.HasValue)
        {
            scale.UpscaleMethod.Set("lanczos");
        }
        bridge.SyncNode(scale);
    }
}
