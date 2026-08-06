using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;

namespace VideoStages.Execution.Graph;

/// <summary>Creates image scale nodes and retargets an unused input scale when safe.</summary>
internal static class ImageScaleReuse
{
    /// <summary>Creates an ImageScale node scaling <paramref name="sourcePath"/> to (width, height).
    /// <paramref name="crop"/> and <paramref name="upscaleMethod"/> are per-caller; the crop differs
    /// between call sites (center vs disabled).</summary>
    public static ImageScaleNode Create(
        WorkflowBridge bridge,
        JArray sourcePath,
        int width,
        int height,
        string crop,
        string upscaleMethod = "lanczos")
    {
        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode().With(
            Width: width,
            Height: height,
            UpscaleMethod: upscaleMethod,
            Crop: crop));
        scale.Image.ConnectFromPath(bridge, sourcePath);
        return scale;
    }

    /// <summary>Scales <paramref name="sourcePath"/> to (width, height). When the source is itself an
    /// ImageScale whose output nothing consumes yet, re-fits that node in place — one resample from
    /// the raw source instead of two chained ones (its crop is kept: a conform's center crop frames
    /// the enlarged dims identically since the aspect is preserved). Otherwise creates a new node.</summary>
    public static ImageScaleNode RetargetOrCreate(
        WorkflowBridge bridge,
        JArray sourcePath,
        int width,
        int height,
        string crop,
        string upscaleMethod)
    {
        if (sourcePath is { Count: 2 }
            && bridge.NodeAt(sourcePath) is ImageScaleNode existing
            && bridge.ResolvePath(sourcePath) is INodeOutput output
            && !bridge.Graph.FindDownstream(output).Any())
        {
            existing.With(Width: width, Height: height, UpscaleMethod: upscaleMethod);
            return existing;
        }
        return Create(bridge, sourcePath, width, height, crop, upscaleMethod);
    }
}
