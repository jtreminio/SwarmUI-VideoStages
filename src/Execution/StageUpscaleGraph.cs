using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages;

internal sealed class StageUpscaleGraph(WorkflowGenerator g)
{
    internal WGNodeData Apply(
        WGNodeData source,
        int targetWidth,
        int targetHeight,
        StageUpscaleMode mode,
        string methodName)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetWidth),
                $"Stage upscale dimensions must be positive, received "
                    + $"{targetWidth}x{targetHeight}.");
        }
        if (string.IsNullOrWhiteSpace(methodName))
        {
            throw new ArgumentException(
                "Stage upscale method must be configured.",
                nameof(methodName));
        }
        if (source.DataType != WGNodeData.DT_IMAGE
            && source.DataType != WGNodeData.DT_VIDEO)
        {
            throw Invariant.Failure(
                $"stage upscaling received '{source.DataType}' instead of decoded media");
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput output = mode switch
        {
            StageUpscaleMode.Pixel => ApplyPixel(
                bridge,
                source,
                targetWidth,
                targetHeight,
                methodName),
            StageUpscaleMode.Model => ApplyModel(
                bridge,
                source,
                targetWidth,
                targetHeight,
                methodName),
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "The shared upscale graph accepts only decoded-media modes."),
        };
        WGNodeData scaled = source.WithPath(output.ToPath());
        scaled.Width = targetWidth;
        scaled.Height = targetHeight;
        g.CurrentMedia = scaled;
        return scaled;
    }

    internal static (int Width, int Height) ResolveTargetDimensions(
        int width,
        int height,
        double factor,
        int requiredMultiple = DimensionSnap.MinimumMultiple)
    {
        (int alignedWidth, int alignedHeight) = AlignScaledDimensions(
            width,
            height,
            factor);
        return DimensionSnap.Snap(
            alignedWidth,
            alignedHeight,
            requiredMultiple);
    }

    internal static (int Width, int Height) AlignScaledDimensions(
        int width,
        int height,
        double factor) =>
        (
            AlignTo16((int)Math.Round(Math.Max(width, 16) * factor)),
            AlignTo16((int)Math.Round(Math.Max(height, 16) * factor))
        );

    private static INodeOutput ApplyPixel(
        WorkflowBridge bridge,
        WGNodeData source,
        int targetWidth,
        int targetHeight,
        string methodName) =>
        ImageScaleReuse.RetargetOrCreate(
            bridge,
            source.Path,
            targetWidth,
            targetHeight,
            "disabled",
            methodName).IMAGE;

    private static INodeOutput ApplyModel(
        WorkflowBridge bridge,
        WGNodeData source,
        int targetWidth,
        int targetHeight,
        string modelName)
    {
        UpscaleModelLoaderNode loader = bridge.AddNode(new UpscaleModelLoaderNode()).With(
            ModelName: modelName);
        ImageUpscaleWithModelNode upscale = bridge.AddNode(new ImageUpscaleWithModelNode().With(
            UpscaleModel: loader.UPSCALEMODEL));
        upscale.Image.ConnectFromPath(bridge, source.Path);
        return bridge.AddNode(new ImageScaleNode().With(
            Width: targetWidth,
            Height: targetHeight,
            UpscaleMethod: "lanczos",
            Crop: "disabled",
            Image: upscale.IMAGE)).IMAGE;
    }

    private static int AlignTo16(int value) =>
        Math.Max(16, Math.Max(value, 16) / 16 * 16);
}
