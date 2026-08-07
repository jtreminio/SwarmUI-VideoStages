namespace VideoStages;

[Flags]
internal enum StageGuideReferenceKind
{
    Unknown = 0,
    Generated = 1 << 0,
    Base = 1 << 1,
    Refiner = 1 << 2,
    PreviousStage = 1 << 3,
    ExplicitStage = 1 << 4,
    Base2Edit = 1 << 5,
}

/// <summary>Only ExplicitStage and Base2Edit carry an index; the request reader pattern-matches
/// on it rather than null-checking.</summary>
internal readonly record struct StageGuideReferenceSelection(
    StageGuideReferenceKind Kind,
    int? ReferencedStageIndex);

/// <summary>The authored grammar for what a stage guides from. Sole owner: the request reader
/// canonicalizes against it, and planning re-reads the canonical value through it.</summary>
internal static class StageGuideReference
{
    internal static StageGuideReferenceSelection Classify(string rawValue)
    {
        string raw = StringUtils.Compact(rawValue);
        if (StringUtils.Equals(raw, MediaSource.Generated))
        {
            return new(StageGuideReferenceKind.Generated, null);
        }
        if (StringUtils.Equals(raw, MediaSource.Base))
        {
            return new(StageGuideReferenceKind.Base, null);
        }
        if (StringUtils.Equals(raw, MediaSource.Refiner))
        {
            return new(StageGuideReferenceKind.Refiner, null);
        }
        if (StringUtils.Equals(raw, MediaSource.PreviousStage))
        {
            return new(StageGuideReferenceKind.PreviousStage, null);
        }
        if (MediaSource.TryParseExplicitStageIndex(raw, out int stageIndex))
        {
            return new(StageGuideReferenceKind.ExplicitStage, stageIndex);
        }
        if (MediaSource.TryParseBase2EditIndex(raw, out int editStageIndex))
        {
            return new(StageGuideReferenceKind.Base2Edit, editStageIndex);
        }
        return new(StageGuideReferenceKind.Unknown, null);
    }
}
