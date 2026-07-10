using VideoStages.LTX2;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Unit tests for <see cref="LtxStageExecutor.TilePromptWindows"/> — the pure geometry that turns
/// begin/end MINOR prompt windows into the Prompt Relay node's end-to-end <c>{prompt, seconds}</c>
/// segment list. Gaps (and blank windows) become blank segments the node later fills with the
/// MAJOR/global prompt.
/// </summary>
public class PromptRelayTilingTests
{
    private static PromptWindowSpec Window(string prompt, double start, double duration) =>
        new(prompt, start, duration);

    [Fact]
    public void Empty_window_list_tiles_to_nothing()
    {
        Assert.Empty(LtxStageExecutor.TilePromptWindows([], clipSeconds: 4));
    }

    [Fact]
    public void Single_mid_clip_window_yields_leading_and_trailing_gaps()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [Window("a red car", start: 1, duration: 1)],
            clipSeconds: 4);

        Assert.Equal(
            [("", 1.0), ("a red car", 1.0), ("", 2.0)],
            tiled);
    }

    [Fact]
    public void Window_covering_whole_clip_is_a_single_segment_so_no_relay()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [Window("solo", start: 0, duration: 4)],
            clipSeconds: 4);

        Assert.Equal([("solo", 4.0)], tiled);
        Assert.True(tiled.Count < 2, "A full-clip single window must not trigger a relay.");
    }

    [Fact]
    public void Two_windows_with_a_gap_between_them_tile_in_order()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [Window("first", start: 0, duration: 1), Window("second", start: 2, duration: 1)],
            clipSeconds: 4);

        Assert.Equal(
            [("first", 1.0), ("", 1.0), ("second", 1.0), ("", 1.0)],
            tiled);
    }

    [Fact]
    public void Windows_are_sorted_by_start_regardless_of_input_order()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [Window("late", start: 2, duration: 1), Window("early", start: 0, duration: 1)],
            clipSeconds: 4);

        Assert.Equal(
            [("early", 1.0), ("", 1.0), ("late", 1.0), ("", 1.0)],
            tiled);
    }

    [Fact]
    public void A_lone_blank_window_leaves_no_active_minor_so_no_tiling()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [Window("   ", start: 1, duration: 1)],
            clipSeconds: 4);

        Assert.Empty(tiled);
    }

    [Fact]
    public void Overlapping_windows_resolve_first_wins()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [Window("first", start: 0, duration: 2), Window("second", start: 1, duration: 2)],
            clipSeconds: 4);

        // "second" starts inside "first"; it only claims the uncovered [2,3] span.
        Assert.Equal(
            [("first", 2.0), ("second", 1.0), ("", 1.0)],
            tiled);
    }

    [Fact]
    public void Window_extending_past_clip_end_is_clamped()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [Window("tail", start: 3, duration: 5)],
            clipSeconds: 4);

        Assert.Equal([("", 3.0), ("tail", 1.0)], tiled);
    }

    [Fact]
    public void Window_starting_past_the_clip_end_tiles_to_a_single_full_gap()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [Window("late", start: 12, duration: 3)],
            clipSeconds: 10);

        // The whole clip is one blank gap (MAJOR prompt); the segments still sum to the clip length.
        Assert.Equal([("", 10.0)], tiled);
        Assert.Equal(10.0, tiled.Sum(t => t.Seconds), 5);
    }

    [Fact]
    public void An_in_bounds_window_plus_an_out_of_bounds_one_still_sum_to_the_clip()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [Window("A", start: 0, duration: 6), Window("B", start: 12, duration: 3)],
            clipSeconds: 10);

        Assert.Equal([("A", 6.0), ("", 4.0)], tiled);
        Assert.Equal(10.0, tiled.Sum(t => t.Seconds), 5);
    }

    [Fact]
    public void Every_tiling_sums_to_the_clip_length()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [
                Window("a", start: 1, duration: 1),
                Window("b", start: 2, duration: 1),
                Window("c", start: 6, duration: 2),
            ],
            clipSeconds: 12);

        Assert.Equal(12.0, tiled.Sum(t => t.Seconds), 5);
    }

    [Fact]
    public void Adjacent_windows_with_no_gap_produce_no_blank_between()
    {
        var tiled = LtxStageExecutor.TilePromptWindows(
            [Window("first", start: 0, duration: 2), Window("second", start: 2, duration: 2)],
            clipSeconds: 4);

        Assert.Equal([("first", 2.0), ("second", 2.0)], tiled);
    }
}
