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
            Math.Max(width, 16) * payload.Upscale.Factor));
        int targetHeight = AlignTo16((int)Math.Round(
            Math.Max(height, 16) * payload.Upscale.Factor));
        return SnapForIcLora(stage, targetWidth, targetHeight);
    }

    // IC-LoRAs with a reference-downscale factor hard-error unless pixel dimensions are multiples
    // of 32×factor. The official workflows use the same factor*32 snap.
    public static (int Width, int Height) SnapForIcLora(StagePlan stage, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(stage);
        int multiple = 32 * IcLoraApplicator.MaxKnownIcLoraDownscaleFactor(
            stage.RequireLtx2Payload().IcLoras);
        if (multiple <= 32 || (width % multiple == 0 && height % multiple == 0))
        {
            return (width, height);
        }

        int snappedWidth = Math.Max(multiple, width / multiple * multiple);
        int snappedHeight = Math.Max(multiple, height / multiple * multiple);
        Logs.Info(
            $"VideoStages: stage {stage.StageId} dims {width}x{height} snapped to "
            + $"{snappedWidth}x{snappedHeight} — the active IC-LoRA's reference downscale factor "
            + $"requires multiples of {multiple}.");
        return (snappedWidth, snappedHeight);
    }

    private static int AlignTo16(int value) =>
        Math.Max(16, Math.Max(value, 16) / 16 * 16);
}
