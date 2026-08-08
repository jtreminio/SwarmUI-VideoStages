using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages.Execution.StockHost;

internal sealed record StockHostVideoStagePayload(
    ArchitectureId ArchitectureId,
    string ModelClassId,
    string CompatibilityClassId,
    LoraTarget LoraTarget,
    StageCorePlan Core) :
    IArchitectureStagePayload
{
    public bool ContinuesSamplingFromPreviousStage { get; init; }

    /// <summary>
    /// Compiles the stage payload every stock-host architecture shares. <paramref name="loraTarget"/>
    /// is passed rather than read off <paramref name="resolved"/> because WAN pins one target for
    /// every compat class.
    /// </summary>
    internal static StockHostVideoStagePayload Compile(
        ArchitectureId architectureId,
        ClipSpec clip,
        StageSpec stage,
        ResolvedVideoModel resolved,
        LoraTarget loraTarget) =>
        new(
            architectureId,
            resolved.ModelClassId,
            resolved.CompatibilityClassId,
            loraTarget,
            new StageCorePlan(
                stage.Control,
                stage.Steps,
                stage.CfgScale,
                stage.Sampler,
                stage.Scheduler,
                StageUpscalePlanCompiler.Compile(stage),
                LoraPlanCompiler.Compile(clip, stage, loraTarget)));
}

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
            throw Invariant.Failure(
                $"Stage {stage.StageId} has no {architectureLabel} stock-host payload.");
        }
        return payload;
    }
}
