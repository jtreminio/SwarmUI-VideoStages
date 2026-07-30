using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

/// <summary>
/// Emits the architecture-neutral pixel resize that prepares media for a stage. Callers own
/// upscale policy and target-dimension calculation; this leaf owns only the stock ImageScale
/// graph operation and accurate output metadata.
/// </summary>
internal sealed class StagePixelScaleGraphBuilder(WorkflowGenerator g)
{
    internal WGNodeData Apply(
        WGNodeData source,
        int targetWidth,
        int targetHeight,
        string upscaleMethod)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetWidth),
                $"Stage pixel-scale dimensions must be positive, received "
                    + $"{targetWidth}x{targetHeight}.");
        }
        if (string.IsNullOrWhiteSpace(upscaleMethod))
        {
            throw new ArgumentException(
                "Stage pixel-scale method must be configured.",
                nameof(upscaleMethod));
        }

        if (source.DataType != WGNodeData.DT_IMAGE
            && source.DataType != WGNodeData.DT_VIDEO)
        {
            throw new InvalidOperationException(
                $"Stage pixel scaling requires decoded media, received '{source.DataType}'.");
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        ImageScaleNode scale = ImageScaleReuse.RetargetOrCreate(
            bridge,
            source.Path,
            targetWidth,
            targetHeight,
            "disabled",
            upscaleMethod);
        WGNodeData scaled = source.WithPath(scale.IMAGE.ToPath());
        scaled.Width = targetWidth;
        scaled.Height = targetHeight;
        g.CurrentMedia = scaled;
        return scaled;
    }
}
