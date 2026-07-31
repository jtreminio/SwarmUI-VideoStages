using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class RootVideoStageResizer(
    WorkflowGenerator g,
    RootVideoStageHandoff handoff) : IArchitectureRootMediaResizer
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

    internal static void ApplyRootResolutionBeforeImageToVideo(
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!TryGetApplicableContext(genInfo, out VideoExecutionPlanContext context))
        {
            return;
        }

        context.ExecutePrepared(() =>
        {
            if (!TryGetVideoContextResizerWithRootSize(
                genInfo, out IArchitectureRootMediaResizer resizer, out int width, out int height))
            {
                return;
            }
            genInfo.Width = width;
            genInfo.Height = height;
            resizer.ApplyCurrentMediaResolution(width, height);
        });
    }

    internal static void ApplyRootLatentResolutionAfterImageToVideo(
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!TryGetApplicableContext(genInfo, out VideoExecutionPlanContext context))
        {
            return;
        }

        context.ExecutePrepared(() =>
        {
            if (TryGetVideoContextResizerWithRootSize(
                genInfo, out IArchitectureRootMediaResizer resizer, out int width, out int height))
            {
                resizer.SetCurrentMediaDimensions(width, height);
            }
        });
    }

    private static bool TryGetApplicableContext(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        out VideoExecutionPlanContext context)
    {
        context = null;
        if (!VideoStagesContext.TryGetActiveCoreVideoContext(
                genInfo,
                out _,
                out context))
        {
            return false;
        }
        return context.RootOwnerArchitectureId
            == Ltx2ArchitectureModule.ArchitectureId;
    }

    private static bool TryGetVideoContextResizerWithRootSize(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        out IArchitectureRootMediaResizer resizer,
        out int width,
        out int height)
    {
        resizer = Runner.GetRootMediaResizer(genInfo.Generator);
        if (resizer is null)
        {
            width = 0;
            height = 0;
            return false;
        }
        return resizer.TryGetRootStageResolution(out width, out height);
    }

    public void ApplyConfiguredRootStageResolutionToCurrentMedia()
    {
        if (!TryGetRootStageResolution(out int width, out int height))
        {
            return;
        }
        if (g.RequireVideoExecutionPlanContext().Plan.Root.HostKind
                == HostRootKind.TextToVideoRoot
            || CurrentMediaFeedsSaveImage())
        {
            SetCurrentMediaDimensions(width, height);
            return;
        }

        ApplyCurrentMediaResolution(width, height);
    }

    /// <summary>
    /// Pixel-resizes the current media to the configured timeline resolution, with no text-to-video
    /// metadata shortcut — for flows where the root generation SURVIVES as the clips' shared source
    /// (initVideoClip first clip) instead of being handed off. Left at the core params' size it would
    /// splinter the timeline's resolutions and degrade every overlap-boundary merge to a hard cut.
    /// </summary>
    public void ApplyConfiguredRootStageResolutionToSurvivingRootMedia()
    {
        if (TryGetRootStageResolution(out int width, out int height))
        {
            ApplyCurrentMediaResolution(width, height);
        }
    }

    public bool TryGetRootStageResolution(out int width, out int height)
    {
        VideoExecutionPlan plan = g.GetVideoExecutionPlanContext()?.Plan;
        if (plan?.HasConfiguredResolution == true
            && TryPositiveDimensionPair(plan.Width, plan.Height, out width, out height))
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

    public void SetCurrentMediaDimensions(int width, int height)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }
        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
    }

    public void ApplyCurrentMediaResolution(int width, int height)
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
        if (scale is null)
        {
            if (bridge.ResolvePath(path) is not INodeOutput sourceOutput)
            {
                return;
            }
            scale = bridge.AddNode(new ImageScaleNode());
            scale.Image.ConnectToUntyped(sourceOutput);
            g.CurrentMedia = g.CurrentMedia.WithPath(scale.IMAGE);
        }

        scale.With(
            Width: width,
            Height: height,
            Crop: "center");
        if (!scale.UpscaleMethod.HasValue)
        {
            scale.UpscaleMethod.Set("lanczos");
        }
    }
}
