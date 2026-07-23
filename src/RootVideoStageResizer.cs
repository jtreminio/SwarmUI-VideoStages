using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal sealed class RootVideoStageResizer(
    WorkflowGenerator g,
    RootVideoStageHandoff handoff)
{
    private static int _registered;

    public static void RegisterHandlers()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }
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
        if (g.GetVideoStagesSpec().IsTextToVideo || CurrentMediaFeedsSaveImage())
        {
            SetCurrentMediaDimensions(width, height);
            return;
        }

        ApplyCurrentMediaResolution(width, height);
    }

    /// <summary>
    /// Pixel-resizes the current media to the configured timeline resolution, with no text-to-video
    /// metadata shortcut — for flows where the root generation SURVIVES as the clips' shared source
    /// (sourced first clip) instead of being handed off. Left at the core params' size it would
    /// splinter the timeline's resolutions and degrade every overlap-boundary merge to a hard cut.
    /// </summary>
    internal void ApplyConfiguredRootStageResolutionToSurvivingRootMedia()
    {
        if (TryGetRootStageResolution(out int width, out int height))
        {
            ApplyCurrentMediaResolution(width, height);
        }
    }

    internal bool TryGetRootStageResolution(out int width, out int height)
    {
        (int? rawJsonWidth, int? rawJsonHeight) = VideoStagesSpecParser.GetRawJsonTopLevelDimensions(g);
        if (TryPositiveDimensionPair(rawJsonWidth, rawJsonHeight, out width, out height))
        {
            return true;
        }

        width = 0;
        height = 0;
        return false;
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

        using WorkflowBridge bridge = BridgeSync.For(g);
        ImageScaleNode scale = bridge.NodeAt<ImageScaleNode>(path);
        INodeOutput insertedFrom = null;
        if (scale is null)
        {
            if (bridge.ResolvePath(path) is not INodeOutput sourceOutput)
            {
                return;
            }
            scale = bridge.AddNode(new ImageScaleNode());
            scale.Image.ConnectToUntyped(sourceOutput);
            g.CurrentMedia = g.CurrentMedia.WithPath(scale.IMAGE);
            insertedFrom = sourceOutput;
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

        if (insertedFrom is not null)
        {
            // A save watching the pre-conform root output must follow it through the inserted
            // scale: every later save retarget (stage handoff, cross-clip merge) matches the
            // post-scale path only, so a save left on the raw output would ship the unconformed
            // root generation as an extra unrelated video.
            SaveAnimationRetargeter.Retarget(
                bridge,
                save => save.Images.Connection is INodeOutput existing
                    && existing.Node.Id == insertedFrom.Node.Id
                    && existing.SlotIndex == insertedFrom.SlotIndex,
                bridge.ResolvePath(WorkflowBridge.ToPath(scale.IMAGE)),
                newAudio: null,
                retargetAudio: false);
        }
    }
}
