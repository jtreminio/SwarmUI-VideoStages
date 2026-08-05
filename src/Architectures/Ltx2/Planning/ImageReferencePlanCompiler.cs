using System.Collections.Immutable;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Compiles authored frame-reference positions, strengths, and source intent.</summary>
internal static class ImageReferencePlanCompiler
{
    internal static ImmutableArray<ImageReferencePlan> Compile(ClipSpec clip, StageSpec stage)
    {
        ImmutableArray<ImageReferencePlan>.Builder plans =
            ImmutableArray.CreateBuilder<ImageReferencePlan>();
        IReadOnlyList<ImageRefSpec> references = clip.ImageRefs ?? [];
        IReadOnlyList<double> strengths = stage.ImageRefStrengths ?? [];
        for (int i = 0; i < references.Count; i++)
        {
            ImageRefSpec reference = references[i];
            (ImageReferenceSourceKind sourceKind, int? editStage) = CompileSource(reference.Source);
            plans.Add(new ImageReferencePlan(
                sourceKind,
                reference.Source?.Trim() ?? "",
                editStage,
                reference.Frame,
                reference.FromEnd ? ImageReferenceFrameEdge.End : ImageReferenceFrameEdge.Start,
                i < strengths.Count ? strengths[i] : Constants.DefaultStageRefStrength,
                reference.UploadFileName,
                reference.Data));
        }
        return plans.ToImmutable();
    }

    private static (ImageReferenceSourceKind Kind, int? EditStage) CompileSource(string rawSource)
    {
        if (StringUtils.Equals(rawSource, "Upload"))
        {
            return (ImageReferenceSourceKind.Upload, null);
        }
        if (StringUtils.Equals(rawSource, "Base"))
        {
            return (ImageReferenceSourceKind.Base, null);
        }
        if (StringUtils.Equals(rawSource, "Refiner"))
        {
            return (ImageReferenceSourceKind.Refiner, null);
        }
        if (ImageReferenceSyntax.TryParseBase2EditStageIndex(rawSource, out int editStage))
        {
            return (ImageReferenceSourceKind.Base2Edit, editStage);
        }
        return (ImageReferenceSourceKind.Unknown, null);
    }
}
