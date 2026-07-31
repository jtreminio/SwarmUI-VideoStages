using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.HostVideo;

/// <summary>
/// The proven common settings consumed by SwarmUI's stock video parameter seam. Architecture
/// payloads can carry additional facts, but WAN and generic-host stages both expose this subset.
/// </summary>
internal interface IHostVideoStageSettings
{
    string Model { get; }

    double Control { get; }

    int Steps { get; }

    double CfgScale { get; }

    string Sampler { get; }

    string Scheduler { get; }

    StageUpscalePlan Upscale { get; }
}

internal sealed record StockHostVideoStagePayload(
    ArchitectureId ArchitectureId,
    string Model,
    string ModelClassId,
    string CompatibilityClassId,
    NormalLoraTargetPolicy LoraTargetPolicy,
    double Control,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler,
    StageUpscalePlan Upscale,
    ImmutableArray<NormalLoraPlan> Loras) :
    IArchitectureStagePayload,
    IHostVideoStageSettings;

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
            IHostVideoStageSettings settings = stage.ArchitecturePayload
                as IHostVideoStageSettings
                ?? throw new InvalidOperationException(
                    $"Stage {stage.StageId} has no stock host-video settings.");
            StageUpscalePlan upscale = settings.Upscale;
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
