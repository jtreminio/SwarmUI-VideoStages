namespace VideoStages.Planning;

/// <summary>
/// Pixel-frame grid arithmetic for a known, static generated-video request. This must not be used
/// for initVideoClip media lengths, runtime-derived counts, boundary windows, or timeline conformance.
/// </summary>
internal static class StaticGeneratedFrameGrid
{
    /// <summary>
    /// Whether a positive static generated pixel-frame request is exactly representable on the
    /// architecture grid, whose first pixel frame occupies the grid origin.
    /// </summary>
    internal static bool IsAligned(int requestedPixelFrames, int frameGrid)
    {
        ValidateGrid(frameGrid);
        return requestedPixelFrames >= 1
            && (requestedPixelFrames - 1) % frameGrid == 0;
    }

    /// <summary>
    /// Snaps a known static generated pixel-frame request down to the nearest architecture-grid
    /// count.
    /// Non-positive requests retain Wan's existing minimum-one-frame behavior.
    /// </summary>
    internal static int SnapDown(int requestedPixelFrames, int frameGrid)
    {
        ValidateGrid(frameGrid);
        return 1 + (Math.Max(1, requestedPixelFrames) - 1) / frameGrid * frameGrid;
    }

    /// <summary>
    /// Snaps a known static generated request up so the effective duration never becomes shorter
    /// than the authored duration.
    /// </summary>
    internal static int SnapUp(int requestedPixelFrames, int frameGrid)
    {
        ValidateGrid(frameGrid);
        int intervals = Math.Max(1, requestedPixelFrames) - 1;
        int blocks = intervals / frameGrid + (intervals % frameGrid == 0 ? 0 : 1);
        return checked(1 + checked(blocks * frameGrid));
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

    private static void ValidateGrid(int frameGrid)
    {
        if (frameGrid < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameGrid),
                frameGrid,
                "A static generated frame grid must be at least one.");
        }
    }
}
