using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class RootVideoStageResizer(WorkflowGenerator g)
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
            RootVideoStageResizer resizer = Create(genInfo.Generator);
            if (!resizer.TryGetRootStageResolution(out int width, out int height))
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
            RootVideoStageResizer resizer = Create(genInfo.Generator);
            if (resizer.TryGetRootStageResolution(out int width, out int height))
            {
                resizer.SetCurrentMediaDimensions(width, height);
            }
        });
    }

    private static RootVideoStageResizer Create(WorkflowGenerator g) =>
        new(g);

    private static bool TryGetApplicableContext(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        out VideoExecutionPlanContext context)
    {
        context = null;
        if (!RequestCaches.TryGetActiveCoreVideoContext(
                genInfo,
                out _,
                out context))
        {
            return false;
        }
        return context.RootOwnerArchitectureId
            == Ltx2ArchitectureModule.ArchitectureId;
    }

    public void ApplyConfiguredRootStageDimensionsToCurrentMedia()
    {
        if (!TryGetRootStageResolution(out int width, out int height))
        {
            return;
        }
        SetCurrentMediaDimensions(width, height);
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
