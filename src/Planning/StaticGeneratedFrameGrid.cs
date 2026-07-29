namespace VideoStages.Planning;

/// <summary>
/// Pixel-frame grid arithmetic for a known, static generated-video request. This must not be used
/// for sourced media lengths, runtime-derived counts, boundary windows, or timeline conformance.
/// </summary>
internal static class StaticGeneratedFrameGrid
{
    /// <summary>
    /// Whether a positive static generated pixel-frame request is exactly representable on the
    /// profile grid, whose first pixel frame occupies the grid origin.
    /// </summary>
    internal static bool IsAligned(int requestedPixelFrames, int frameGrid)
    {
        ValidateGrid(frameGrid);
        return requestedPixelFrames >= 1
            && (requestedPixelFrames - 1) % frameGrid == 0;
    }

    /// <summary>
    /// Snaps a known static generated pixel-frame request down to the nearest profile-grid count.
    /// Non-positive requests retain Wan's existing minimum-one-frame behavior.
    /// </summary>
    internal static int SnapDown(int requestedPixelFrames, int frameGrid)
    {
        ValidateGrid(frameGrid);
        return 1 + (Math.Max(1, requestedPixelFrames) - 1) / frameGrid * frameGrid;
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
