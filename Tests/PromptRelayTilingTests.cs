using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>Tests prompt-window tiling into relay segments.</summary>
[Collection("VideoStagesTests")]
public class PromptRelayTilingTests
{
    private static PromptWindowPlan Window(string prompt, double start, double duration) =>
        new(prompt, start, duration, start + duration);

    private static List<(string Prompt, double Seconds)> Values(
        IEnumerable<PromptRelaySegmentPlan> segments) =>
        segments.Select(segment => (segment.Prompt, segment.Seconds)).ToList();

    [Fact]
    public void Empty_window_list_tiles_to_nothing()
    {
        Assert.Empty(PromptRelayPlanCompiler.Tile([], clipSeconds: 4));
    }

    [Fact]
    public void Single_mid_clip_window_yields_leading_and_trailing_gaps()
    {
        var tiled = PromptRelayPlanCompiler.Tile(
            [Window("a red car", start: 1, duration: 1)],
            clipSeconds: 4);

        Assert.Equal(
            [("", 1.0), ("a red car", 1.0), ("", 2.0)],
            Values(tiled));
    }

    [Fact]
    public void Window_covering_whole_clip_is_a_single_segment_so_no_relay()
    {
        var tiled = PromptRelayPlanCompiler.Tile(
            [Window("solo", start: 0, duration: 4)],
            clipSeconds: 4);

        Assert.Equal([("solo", 4.0)], Values(tiled));
        Assert.True(tiled.Length < 2, "A full-clip single window must not trigger a relay.");
    }

    [Fact]
    public void Two_windows_with_a_gap_between_them_tile_in_order()
    {
        var tiled = PromptRelayPlanCompiler.Tile(
            [Window("first", start: 0, duration: 1), Window("second", start: 2, duration: 1)],
            clipSeconds: 4);

        Assert.Equal(
            [("first", 1.0), ("", 1.0), ("second", 1.0), ("", 1.0)],
            Values(tiled));
    }

    [Fact]
    public void Windows_are_sorted_by_start_regardless_of_input_order()
    {
        var tiled = PromptRelayPlanCompiler.Tile(
            [Window("late", start: 2, duration: 1), Window("early", start: 0, duration: 1)],
            clipSeconds: 4);

        Assert.Equal(
            [("early", 1.0), ("", 1.0), ("late", 1.0), ("", 1.0)],
            Values(tiled));
    }

    [Fact]
    public void A_lone_blank_window_leaves_no_active_minor_so_no_tiling()
    {
        var tiled = PromptRelayPlanCompiler.Tile(
            [Window("   ", start: 1, duration: 1)],
            clipSeconds: 4);

        Assert.Empty(tiled);
    }

    [Fact]
    public void Overlapping_windows_resolve_first_wins()
    {
        var tiled = PromptRelayPlanCompiler.Tile(
            [Window("first", start: 0, duration: 2), Window("second", start: 1, duration: 2)],
            clipSeconds: 4);

        // "second" starts inside "first"; it only claims the uncovered [2,3] span.
        Assert.Equal(
            [("first", 2.0), ("second", 1.0), ("", 1.0)],
            Values(tiled));
    }

    [Fact]
    public void Window_extending_past_clip_end_is_clamped()
    {
        var tiled = PromptRelayPlanCompiler.Tile(
            [Window("tail", start: 3, duration: 5)],
            clipSeconds: 4);

        Assert.Equal([("", 3.0), ("tail", 1.0)], Values(tiled));
    }

    [Fact]
    public void Window_starting_past_the_clip_end_tiles_to_a_single_full_gap()
    {
        var tiled = PromptRelayPlanCompiler.Tile(
            [Window("late", start: 12, duration: 3)],
            clipSeconds: 10);

        Assert.Equal([("", 10.0)], Values(tiled));
        Assert.Equal(10.0, tiled.Sum(t => t.Seconds), 5);
    }

    [Fact]
    public void An_in_bounds_window_plus_an_out_of_bounds_one_still_sum_to_the_clip()
    {
        var tiled = PromptRelayPlanCompiler.Tile(
            [Window("A", start: 0, duration: 6), Window("B", start: 12, duration: 3)],
            clipSeconds: 10);

        Assert.Equal([("A", 6.0), ("", 4.0)], Values(tiled));
        Assert.Equal(10.0, tiled.Sum(t => t.Seconds), 5);
    }

    [Fact]
    public void Every_tiling_sums_to_the_clip_length()
    {
        var tiled = PromptRelayPlanCompiler.Tile(
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
        var tiled = PromptRelayPlanCompiler.Tile(
            [Window("first", start: 0, duration: 2), Window("second", start: 2, duration: 2)],
            clipSeconds: 4);

        Assert.Equal([("first", 2.0), ("second", 2.0)], Values(tiled));
    }
}
