namespace VideoStages.Planning;

/// <summary>
/// Pixel-frame grid arithmetic for a known, static generated-video request. This must not be used
/// for initVideoClip media lengths, runtime-derived counts, boundary windows, or timeline conformance.
/// </summary>
internal static class StaticGeneratedFrameGrid
{
    /// <summary>
    /// Whether a positive static generated pixel-frame request is exactly representable on the
    /// architecture grid. <paramref name="gridOrigin"/> is the smallest frame count the grid can
    /// express, so counts are <c>k * frameGrid + gridOrigin</c>.
    /// </summary>
    internal static bool IsAligned(int requestedPixelFrames, int frameGrid, int gridOrigin = 1)
    {
        ValidateGrid(frameGrid, gridOrigin);
        return requestedPixelFrames >= gridOrigin
            && (requestedPixelFrames - gridOrigin) % frameGrid == 0;
    }

    /// <summary>
    /// Snaps a known static generated pixel-frame request down to the nearest architecture-grid
    /// count.
    /// Non-positive requests retain Wan's existing minimum-one-frame behavior.
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
    /// Returns the smallest grid satisfying every active stage handler. A mixed 6/8 request, for
    /// example, resolves to 24 rather than silently inheriting whichever architecture was globally
    /// coarsest.
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
