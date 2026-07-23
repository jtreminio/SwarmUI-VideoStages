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

    public static int NormalizeOverlap(int overlap) => Math.Clamp(
        overlap < Constants.ContinueOverlapDefaultFrames
            ? Constants.ContinueOverlapDefaultFrames
            : overlap - (overlap % 8),
        Constants.ContinueOverlapDefaultFrames,
        Constants.ContinueOverlapMaxFrames);

    public static BoundaryExecutionMode ParsePlanMode(string value, out bool isKnown)
    {
        if (string.Equals(value, Constants.BoundaryOutContinue, StringComparison.OrdinalIgnoreCase))
        {
            isKnown = true;
            return BoundaryExecutionMode.Continue;
        }
        if (string.Equals(value, Constants.BoundaryOutCrossfade, StringComparison.OrdinalIgnoreCase))
        {
            isKnown = true;
            return BoundaryExecutionMode.Crossfade;
        }
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, Constants.BoundaryOutCut, StringComparison.OrdinalIgnoreCase))
        {
            isKnown = true;
            return BoundaryExecutionMode.Cut;
        }
        isKnown = false;
        return BoundaryExecutionMode.Cut;
    }
}
