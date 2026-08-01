using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

/// <summary>
/// Emits the architecture-neutral upscale-model resize that prepares media for a stage. The
/// loader and its fixed-ratio upscale are stock ComfyUI operations, so every architecture that
/// holds decoded media can drive them; callers own upscale policy and target dimensions.
/// </summary>
internal sealed class StageModelUpscaleGraphBuilder(WorkflowGenerator g)
{
    internal WGNodeData Apply(
        WGNodeData source,
        int targetWidth,
        int targetHeight,
        string modelName)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.DataType != WGNodeData.DT_IMAGE
            && source.DataType != WGNodeData.DT_VIDEO)
        {
            throw new InvalidOperationException(
                $"Stage model upscaling requires decoded media, received '{source.DataType}'.");
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        UpscaleModelLoaderNode loader = bridge.AddNode(new UpscaleModelLoaderNode()).With(
            ModelName: modelName);

        ImageUpscaleWithModelNode upscale = bridge.AddNode(new ImageUpscaleWithModelNode().With(
            UpscaleModel: loader.UPSCALEMODEL));
        upscale.Image.ConnectFromPath(bridge, source.Path);

        ImageScaleNode fit = bridge.AddNode(new ImageScaleNode().With(
            Width: targetWidth,
            Height: targetHeight,
            UpscaleMethod: "lanczos",
            Crop: "disabled",
            Image: upscale.IMAGE));
        WGNodeData scaled = source.WithPath(fit.IMAGE.ToPath());
        scaled.Width = targetWidth;
        scaled.Height = targetHeight;
        g.CurrentMedia = scaled;
        return scaled;
    }
}
