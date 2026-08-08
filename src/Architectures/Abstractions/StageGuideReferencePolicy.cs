namespace VideoStages.Architectures.Abstractions;

/// <summary>Which members of the <see cref="StageGuideReference"/> grammar an architecture
/// accepts.</summary>
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

    internal bool Allows(StageGuideReferenceSelection selection) =>
        selection.Kind != StageGuideReferenceKind.Unknown
        && (AllowedKinds & selection.Kind) != 0;
}
