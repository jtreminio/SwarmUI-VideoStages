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
            LogSnap(
                stage.StageId,
                SnapReason(multiple),
                alignedWidth,
                alignedHeight,
                targetWidth,
                targetHeight);
        }
        return (targetWidth, targetHeight);
    }

    // IC-LoRA reference downscaling raises the grid requirement to MinimumMultiple×factor.
    public static (int Width, int Height) SnapForIcLora(StagePlan stage, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (!TrySnap(
                stage.RequireLtx2Payload().IcLoras,
                width,
                height,
                out (int Width, int Height) snapped,
                out string reason))
        {
            return (width, height);
        }

        LogSnap(stage.StageId, reason, width, height, snapped.Width, snapped.Height);
        return snapped;
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
        if (!TrySnap(icLoras, width, height, out (int Width, int Height) snapped, out string reason))
        {
            return null;
        }

        return new(
            PlanDiagnosticSeverity.Info,
            "ltx.dimension_snapped",
            $"dimensions {width}x{height} will snap to {snapped.Width}x{snapped.Height}; {reason}",
            clipId,
            stageId);
    }

    private static bool TrySnap(
        IEnumerable<IcLoraPlan> icLoras,
        int width,
        int height,
        out (int Width, int Height) snapped,
        out string reason)
    {
        int multiple = RequiredMultiple(icLoras);
        snapped = DimensionSnap.Snap(width, height, multiple);
        reason = SnapReason(multiple);
        return snapped != (width, height);
    }

    private static string SnapReason(int multiple) =>
        multiple > DimensionSnap.MinimumMultiple
            ? $"the active IC-LoRA's reference downscale factor requires multiples of {multiple}"
            : $"the VideoStages pixel grid requires multiples of {DimensionSnap.MinimumMultiple}";

    private static void LogSnap(
        int stageId,
        string reason,
        int width,
        int height,
        int snappedWidth,
        int snappedHeight) =>
        Logs.Info(
            $"VideoStages: stage {stageId} dims {width}x{height} snapped to "
            + $"{snappedWidth}x{snappedHeight} — {reason}.");
}
