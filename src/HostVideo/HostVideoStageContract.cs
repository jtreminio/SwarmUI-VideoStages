using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.HostVideo;

/// <summary>
/// Stock-host-only model facts paired with the common generated-stage settings.
/// </summary>
internal sealed record StockHostVideoStagePayload(
    ArchitectureId ArchitectureId,
    string ModelClassId,
    string CompatibilityClassId,
    NormalLoraTargetPolicy LoraTargetPolicy,
    StageCorePlan Core) :
    IArchitectureStagePayload;

internal static class StockHostVideoStagePayloadExtensions
{
    internal static StockHostVideoStagePayload RequireStockHostVideoPayload(
        this StagePlan stage,
        ArchitectureId architectureId,
        string architectureLabel)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.ArchitecturePayload is not StockHostVideoStagePayload payload
            || payload.ArchitectureId != architectureId)
        {
            throw new InvalidOperationException(
                $"Stage {stage.StageId} has no {architectureLabel} stock host-video payload.");
        }
        return payload;
    }
}

/// <summary>
/// Shared sampler-step arithmetic for stock-host decoded-video refinement.
/// </summary>
internal static class HostVideoStageSchedulePolicy
{
    internal static int StartStep(int steps, double control) =>
        (int)Math.Floor(steps * (1 - control));

    internal static bool IsQuantizedZeroPartial(int steps, double control) =>
        control < 1 && StartStep(steps, control) == 0;
}

internal static class HostVideoStageGeometry
{
    internal static (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height)
    {
        foreach (StagePlan stage in stages ?? [])
        {
            StageUpscalePlan upscale = stage.Core.Upscale;
            if (upscale?.Mode != StageUpscaleMode.Pixel
                || stage.Input is not (
                    StageInputKind.SourceVideo
                    or StageInputKind.PreviousStage))
            {
                continue;
            }
            (width, height) = DimensionSnap.Snap(
                width * upscale.Factor,
                height * upscale.Factor);
        }
        return (width, height);
    }
}

/// <summary>
/// Conditional authoring rules shared by architectures that execute decoded stages through
/// SwarmUI's stock host-video path.
/// </summary>
internal static class HostVideoStageRules
{
    internal static string NormalLoraRequiresSamplingStageCode { get; } =
        ArchitectureFeatureVocabulary.RuleCode(
            ConditionalRuleCodeId.NormalLoraRequiresSamplingStage);

    internal const string NormalLoraRequiresSamplingStageReason =
        "Normal LoRAs require a sampling stage and cannot have nonzero weight on a samplerless passthrough.";

    internal static RuleDecision NormalLoraRequiresSamplingStage { get; } =
        RuleDecision.Conditional(
            NormalLoraRequiresSamplingStageCode,
            NormalLoraRequiresSamplingStageReason,
            RuleScope.Stage,
            new MinimumStageControlRuleConstraints(0));
}
