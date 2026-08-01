using VideoStages.Planning;

namespace VideoStages;

/// <summary>Single normalization and plan-mode mapping for authored clip boundaries.</summary>
internal static class BoundaryPolicy
{
    public static string NormalizeAuthoredMode(string raw)
    {
        string compact = StringUtils.Compact(raw);
        if (compact.Length == 0)
        {
            return Constants.BoundaryOutCut;
        }
        if (StringUtils.Equals(compact, Constants.BoundaryOutContinue))
        {
            return Constants.BoundaryOutContinue;
        }
        if (StringUtils.Equals(compact, Constants.BoundaryOutCrossfade))
        {
            return Constants.BoundaryOutCrossfade;
        }
        return Constants.BoundaryOutCut;
    }

    public static BoundaryJoinType ParsePlanMode(string value)
    {
        if (string.Equals(value, Constants.BoundaryOutContinue, StringComparison.OrdinalIgnoreCase))
        {
            return BoundaryJoinType.Continue;
        }
        if (string.Equals(value, Constants.BoundaryOutCrossfade, StringComparison.OrdinalIgnoreCase))
        {
            return BoundaryJoinType.Crossfade;
        }
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, Constants.BoundaryOutCut, StringComparison.OrdinalIgnoreCase))
        {
            return BoundaryJoinType.Cut;
        }
        return BoundaryJoinType.Cut;
    }
}
