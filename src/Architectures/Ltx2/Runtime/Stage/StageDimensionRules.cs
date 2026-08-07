using SwarmUI.Utils;
using VideoStages.Execution;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2.Runtime.Stage;

internal static class StageDimensionRules
{
    public static (int Width, int Height) ResolveUpscaled(
        StagePlan stage,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(stage);
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        int multiple = RequiredMultiple(payload.IcLoras);
        (int alignedWidth, int alignedHeight) = StageUpscaleGraph.AlignScaledDimensions(
            width,
            height,
            payload.Core.Upscale.Factor);
        (int targetWidth, int targetHeight) = StageUpscaleGraph.ResolveTargetDimensions(
            width,
            height,
            payload.Core.Upscale.Factor,
            multiple);
        if (targetWidth != alignedWidth || targetHeight != alignedHeight)
        {
            LogSnap(stage.StageId, multiple, alignedWidth, alignedHeight, targetWidth, targetHeight);
        }
        return (targetWidth, targetHeight);
    }

    // IC-LoRA reference downscaling raises the /32 grid requirement to 32×factor.
    public static (int Width, int Height) SnapForIcLora(StagePlan stage, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(stage);
        IReadOnlyList<IcLoraPlan> icLoras = stage.RequireLtx2Payload().IcLoras;
        int multiple = RequiredMultiple(icLoras);
        (int snappedWidth, int snappedHeight) = DimensionSnap.Snap(width, height, multiple);
        if (snappedWidth == width && snappedHeight == height)
        {
            return (width, height);
        }

        LogSnap(stage.StageId, multiple, width, height, snappedWidth, snappedHeight);
        return (snappedWidth, snappedHeight);
    }

    internal static int RequiredMultiple(IEnumerable<IcLoraPlan> icLoras)
    {
        int factor = (icLoras ?? [])
            .Select(plan => plan.DimensionDownscaleFactor)
            .DefaultIfEmpty(1)
            .Max();
        return DimensionSnap.MinimumMultiple * Math.Max(1, factor);
    }

    internal static PlanDiagnostic SnapDiagnostic(
        int clipId,
        int stageId,
        IEnumerable<IcLoraPlan> icLoras,
        int width,
        int height)
    {
        int multiple = RequiredMultiple(icLoras);
        (int snappedWidth, int snappedHeight) = DimensionSnap.Snap(width, height, multiple);
        if (snappedWidth == width && snappedHeight == height)
        {
            return null;
        }

        int factor = multiple / DimensionSnap.MinimumMultiple;
        string reason = factor > 1
            ? $"IC-LoRA ×{factor} requires multiples of {multiple}"
            : "VideoStages requires multiples of 32";
        return new(
            PlanDiagnosticSeverity.Info,
            "ltx.dimension_snapped",
            $"dimensions {width}x{height} will snap to {snappedWidth}x{snappedHeight}; {reason}",
            clipId,
            stageId);
    }

    private static void LogSnap(
        int stageId,
        int multiple,
        int width,
        int height,
        int snappedWidth,
        int snappedHeight)
    {
        int factor = multiple / DimensionSnap.MinimumMultiple;
        string reason = factor > 1
            ? $"the active IC-LoRA's reference downscale factor requires multiples of {multiple}"
            : "the VideoStages pixel grid requires multiples of 32";
        Logs.Info(
            $"VideoStages: stage {stageId} dims {width}x{height} snapped to "
            + $"{snappedWidth}x{snappedHeight} — {reason}.");
    }
}
