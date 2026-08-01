namespace VideoStages;

/// <summary>
/// Pixel-grid snapping mirrored by <c>frontend/dimensionSnap.ts</c> and checked by a shared fixture.
/// </summary>
internal static class DimensionSnap
{
    internal const int MinimumMultiple = 32;
    internal const int MaximumDimension = 4096;

    private const double AreaDriftWeight = 0.15;
    private const double ScoreEpsilon = 1e-12;

    internal static (int Width, int Height) Snap(
        double width,
        double height,
        int multiple = MinimumMultiple)
    {
        double requestedWidth = double.IsFinite(width) ? Math.Max(1, width) : 1;
        double requestedHeight = double.IsFinite(height) ? Math.Max(1, height) : 1;
        int grid = Math.Clamp(multiple, MinimumMultiple, MaximumDimension);
        int floorWidth = ClampToSnapRange(
            (int)Math.Floor(requestedWidth / grid) * grid,
            grid);
        int ceilWidth = ClampToSnapRange(
            (int)Math.Ceiling(requestedWidth / grid) * grid,
            grid);
        int floorHeight = ClampToSnapRange(
            (int)Math.Floor(requestedHeight / grid) * grid,
            grid);
        int ceilHeight = ClampToSnapRange(
            (int)Math.Ceiling(requestedHeight / grid) * grid,
            grid);
        (int Width, int Height)[] candidates =
        [
            (floorWidth, floorHeight),
            (ceilWidth, floorHeight),
            (floorWidth, ceilHeight),
            (ceilWidth, ceilHeight),
        ];
        double targetAspect = requestedWidth / requestedHeight;
        double targetArea = requestedWidth * requestedHeight;

        (int Width, int Height) best = candidates[0];
        double bestScore = Score(best, targetAspect, targetArea);
        foreach ((int Width, int Height) candidate in candidates.Skip(1))
        {
            double candidateScore = Score(candidate, targetAspect, targetArea);
            long candidateArea = (long)candidate.Width * candidate.Height;
            long bestArea = (long)best.Width * best.Height;
            if (candidateScore < bestScore - ScoreEpsilon
                || (Math.Abs(candidateScore - bestScore) <= ScoreEpsilon
                    && candidateArea > bestArea))
            {
                best = candidate;
                bestScore = candidateScore;
            }
        }
        return best;
    }

    private static int ClampToSnapRange(int value, int multiple) =>
        Math.Clamp(value, multiple, MaximumDimension);

    private static double Score(
        (int Width, int Height) candidate,
        double targetAspect,
        double targetArea) =>
        Math.Abs(Math.Log((double)candidate.Width / candidate.Height) - Math.Log(targetAspect))
        + AreaDriftWeight * Math.Abs(
            Math.Log(((double)candidate.Width * candidate.Height) / targetArea));
}
