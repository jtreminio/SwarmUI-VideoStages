namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Classifies a stage's guide-reference selector without resolving runtime media.</summary>
internal static class GuideReferencePlanCompiler
{
    internal static GuideReferencePlan Compile(string rawValue)
    {
        string raw = rawValue?.Trim() ?? "";
        if (StringUtils.Equals(raw, "Base"))
        {
            return new(GuideReferenceKind.Base, raw, null);
        }
        if (StringUtils.Equals(raw, "Refiner"))
        {
            return new(GuideReferenceKind.Refiner, raw, null);
        }
        if (StringUtils.Equals(raw, "Generated"))
        {
            return new(GuideReferenceKind.Generated, raw, null);
        }
        if (StringUtils.Equals(raw, "PreviousStage"))
        {
            return new(GuideReferenceKind.PreviousStage, raw, null);
        }
        if (ImageReference.TryParseExplicitStageIndex(raw, out int stageIndex))
        {
            return new(GuideReferenceKind.ExplicitStage, raw, stageIndex);
        }
        if (ImageReference.TryParseBase2EditStageIndex(raw, out int editStageIndex))
        {
            return new(GuideReferenceKind.Base2Edit, raw, editStageIndex);
        }
        return new(GuideReferenceKind.Unknown, raw, null);
    }
}
