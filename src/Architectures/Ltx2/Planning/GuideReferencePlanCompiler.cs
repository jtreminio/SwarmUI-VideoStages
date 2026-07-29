using VideoStages.Architectures.Abstractions;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Classifies a stage's guide-reference selector without resolving runtime media.</summary>
internal static class GuideReferencePlanCompiler
{
    internal static GuideReferencePlan Compile(string rawValue)
    {
        string raw = rawValue?.Trim() ?? "";
        StageGuideReferenceSelection selection = StageGuideReferencePolicy.Classify(raw);
        return new(selection.Kind, raw, selection.ReferencedStageIndex);
    }
}
