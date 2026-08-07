using VideoStages.Architectures.Abstractions;
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
