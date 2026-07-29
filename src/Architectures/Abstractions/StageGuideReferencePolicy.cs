namespace VideoStages.Architectures.Abstractions;

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

internal readonly record struct StageGuideReferenceSelection(
    StageGuideReferenceKind Kind,
    int? ReferencedStageIndex);

/// <summary>
/// Declares which effective, parser-normalized stage guide selectors an architecture consumes.
/// Selector syntax and stage-order validity remain owned by <see cref="VideoStageSpecParser"/>.
/// </summary>
internal readonly record struct StageGuideReferencePolicy
{
    private const StageGuideReferenceKind AllKnownKinds =
        StageGuideReferenceKind.Generated
        | StageGuideReferenceKind.Base
        | StageGuideReferenceKind.Refiner
        | StageGuideReferenceKind.PreviousStage
        | StageGuideReferenceKind.ExplicitStage
        | StageGuideReferenceKind.Base2Edit;

    internal static StageGuideReferencePolicy GeneratedOnly { get; } =
        new(StageGuideReferenceKind.Generated);

    internal StageGuideReferencePolicy(StageGuideReferenceKind allowedKinds)
    {
        if ((allowedKinds & ~AllKnownKinds) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(allowedKinds),
                allowedKinds,
                "Stage guide reference policies may contain only known selector kinds.");
        }
        AllowedKinds = allowedKinds;
    }

    internal StageGuideReferenceKind AllowedKinds { get; }

    internal bool Allows(StageGuideReferenceSelection selection)
    {
        int kindValue = (int)selection.Kind;
        bool isSingleKnownKind =
            kindValue > 0
            && (kindValue & (kindValue - 1)) == 0
            && (selection.Kind & ~AllKnownKinds) == 0;
        return isSingleKnownKind
            && (AllowedKinds & selection.Kind) == selection.Kind;
    }

    internal static StageGuideReferenceSelection Classify(string rawValue)
    {
        string raw = rawValue?.Trim() ?? "";
        if (StringUtils.Equals(raw, "Generated"))
        {
            return new(StageGuideReferenceKind.Generated, null);
        }
        if (StringUtils.Equals(raw, "Base"))
        {
            return new(StageGuideReferenceKind.Base, null);
        }
        if (StringUtils.Equals(raw, "Refiner"))
        {
            return new(StageGuideReferenceKind.Refiner, null);
        }
        if (StringUtils.Equals(raw, "PreviousStage"))
        {
            return new(StageGuideReferenceKind.PreviousStage, null);
        }
        if (ImageReference.TryParseExplicitStageIndex(raw, out int stageIndex))
        {
            return new(StageGuideReferenceKind.ExplicitStage, stageIndex);
        }
        if (ImageReference.TryParseBase2EditStageIndex(raw, out int editStageIndex))
        {
            return new(StageGuideReferenceKind.Base2Edit, editStageIndex);
        }
        return new(StageGuideReferenceKind.Unknown, null);
    }
}
