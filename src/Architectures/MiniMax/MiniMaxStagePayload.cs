using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution.StockHost;
using VideoStages.Planning;

namespace VideoStages.Architectures.MiniMax;

internal sealed record MiniMaxCharacterRefinePlan(
    IReadOnlyList<MiniMaxReferencePlan> PictureReferences,
    MiniMaxReferencePlan IdentityReference,
    string Finder,
    string Subject,
    string Part,
    double SubjectConfidence,
    double PartConfidence,
    int CropWidth,
    int CropHeight);

internal sealed record MiniMaxStagePayload(
    StockHostVideoStagePayload Host,
    MiniMaxCharacterRefinePlan CharacterRefine) : IArchitectureStagePayload
{
    public ArchitectureId ArchitectureId => Host.ArchitectureId;
    public StageCorePlan Core => Host.Core;
    public string ModelClassId => Host.ModelClassId;
    public string CompatibilityClassId => Host.CompatibilityClassId;
    public LoraTarget LoraTarget => Host.LoraTarget;
    public ImmutableArray<FrameRefPlan> FrameReferences => Host.FrameReferences;
}

internal static class MiniMaxStagePayloadExtensions
{
    internal static MiniMaxStagePayload RequireMiniMaxStagePayload(this StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.ArchitecturePayload as MiniMaxStagePayload
            ?? throw Invariant.Failure(
                $"Stage {stage.StageId} has no MiniMax H3 stage payload.");
    }
}
