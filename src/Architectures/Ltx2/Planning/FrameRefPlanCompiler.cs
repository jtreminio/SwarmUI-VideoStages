using System.Collections.Immutable;
using VideoStages.Authoring;

namespace VideoStages.Architectures.Ltx2.Planning;

internal static class FrameRefPlanCompiler
{
    internal static ImmutableArray<FrameRefPlan> Compile(ClipSpec clip, StageSpec stage)
    {
        ImmutableArray<FrameRefPlan>.Builder plans =
            ImmutableArray.CreateBuilder<FrameRefPlan>();
        IReadOnlyList<FrameRefSpec> references = clip.FrameRefs ?? [];
        IReadOnlyList<double> strengths = stage.FrameRefStrengths ?? [];
        for (int i = 0; i < references.Count; i++)
        {
            FrameRefSpec reference = references[i];
            (FrameRefSourceKind sourceKind, int? editStage) = CompileSource(reference.Source);
            plans.Add(new FrameRefPlan(
                sourceKind,
                reference.Source?.Trim() ?? "",
                editStage,
                reference.Frame,
                reference.FromEnd ? FrameRefEdge.End : FrameRefEdge.Start,
                i < strengths.Count ? strengths[i] : Constants.DefaultStageRefStrength,
                reference.UploadFileName,
                reference.Data));
        }
        return plans.ToImmutable();
    }

    private static (FrameRefSourceKind Kind, int? EditStage) CompileSource(string rawSource)
    {
        if (StringUtils.Equals(rawSource, MediaSource.Upload))
        {
            return (FrameRefSourceKind.Upload, null);
        }
        if (StringUtils.Equals(rawSource, MediaSource.Base))
        {
            return (FrameRefSourceKind.Base, null);
        }
        if (StringUtils.Equals(rawSource, MediaSource.Refiner))
        {
            return (FrameRefSourceKind.Refiner, null);
        }
        if (MediaSource.TryParseBase2EditIndex(rawSource, out int editStage))
        {
            return (FrameRefSourceKind.Base2Edit, editStage);
        }
        return (FrameRefSourceKind.Unknown, null);
    }
}
