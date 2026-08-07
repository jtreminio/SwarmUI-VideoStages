namespace VideoStages.Planning;

/// <summary>
/// Pixel-frame grid arithmetic for a known, static generated-video request. This must not be used
/// for initVideoClip media lengths, runtime-derived counts, boundary windows, or timeline conformance.
/// </summary>
internal static class StaticGeneratedFrameGrid
{
    /// <summary>
    /// Snaps a static generated pixel-frame request down to the nearest grid count; a request below
    /// <paramref name="gridOrigin"/> returns the origin.
    /// </summary>
    internal static int SnapDown(int requestedPixelFrames, int frameGrid, int gridOrigin = 1)
    {
        ValidateGrid(frameGrid, gridOrigin);
        int intervals = Math.Max(gridOrigin, requestedPixelFrames) - gridOrigin;
        return gridOrigin + intervals / frameGrid * frameGrid;
    }

    /// <summary>
    /// Snaps a known static generated request up so the effective duration never becomes shorter
    /// than the authored duration.
    /// </summary>
    internal static int SnapUp(int requestedPixelFrames, int frameGrid, int gridOrigin = 1)
    {
        ValidateGrid(frameGrid, gridOrigin);
        int intervals = Math.Max(gridOrigin, requestedPixelFrames) - gridOrigin;
        int blocks = intervals / frameGrid + (intervals % frameGrid == 0 ? 0 : 1);
        return checked(gridOrigin + checked(blocks * frameGrid));
    }

    /// <summary>
    /// The smallest grid satisfying every active stage model. A mixed 6/8 request resolves to 24
    /// rather than the coarsest single grid.
    /// </summary>
    internal static int CompatibleGrid(IEnumerable<int> frameGrids)
    {
        int compatible = 1;
        foreach (int grid in frameGrids ?? [])
        {
            ValidateGrid(grid);
            compatible = checked(compatible / GreatestCommonDivisor(compatible, grid) * grid);
        }
        return compatible;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return left;
    }

    private static void ValidateGrid(int frameGrid, int gridOrigin = 1)
    {
        if (frameGrid < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameGrid),
                frameGrid,
                "A static generated frame grid must be at least one.");
        }
        if (gridOrigin < 1 || gridOrigin > frameGrid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gridOrigin),
                gridOrigin,
                "A static generated frame grid origin must be within [1, frameGrid].");
        }
    }
}
