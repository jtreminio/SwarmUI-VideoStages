using SwarmUI.Utils;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal static class StageDimensionRules
{
    public static (int Width, int Height) ResolveUpscaled(
        StagePlan stage,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(stage);
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        int targetWidth = AlignTo16((int)Math.Round(
            Math.Max(width, 16) * payload.Core.Upscale.Factor));
        int targetHeight = AlignTo16((int)Math.Round(
            Math.Max(height, 16) * payload.Core.Upscale.Factor));
        return SnapForIcLora(stage, targetWidth, targetHeight);
    }

    // Every VideoStages architecture uses the /32 pixel grid. LTX IC-LoRAs with a
    // reference-downscale factor raise that requirement to 32×factor.
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

        int factor = multiple / DimensionSnap.MinimumMultiple;
        string reason = factor > 1
            ? $"the active IC-LoRA's reference downscale factor requires multiples of {multiple}"
            : "the VideoStages pixel grid requires multiples of 32";
        Logs.Info(
            $"VideoStages: stage {stage.StageId} dims {width}x{height} snapped to "
            + $"{snappedWidth}x{snappedHeight} — {reason}.");
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

    private static int AlignTo16(int value) =>
        Math.Max(16, Math.Max(value, 16) / 16 * 16);
}
