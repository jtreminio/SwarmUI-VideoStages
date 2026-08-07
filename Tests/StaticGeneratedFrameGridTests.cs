using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class StaticGeneratedFrameGridTests
{
    [Theory]
    [InlineData(1, 4, 1)]
    [InlineData(2, 4, 1)]
    [InlineData(4, 4, 1)]
    [InlineData(5, 4, 5)]
    [InlineData(8, 4, 5)]
    [InlineData(9, 4, 9)]
    [InlineData(16, 4, 13)]
    [InlineData(17, 4, 17)]
    [InlineData(1, 8, 1)]
    [InlineData(8, 8, 1)]
    [InlineData(9, 8, 9)]
    [InlineData(16, 8, 9)]
    [InlineData(17, 8, 17)]
    public void Snap_down_matches_static_generated_pixel_frame_grid(
        int requested,
        int grid,
        int expected)
    {
        Assert.Equal(expected, StaticGeneratedFrameGrid.SnapDown(requested, grid));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Snap_down_preserves_minimum_one_frame_behavior(int requested)
    {
        Assert.Equal(1, StaticGeneratedFrameGrid.SnapDown(requested, 4));
    }

    [Theory]
    [InlineData(1, 6, 1)]
    [InlineData(2, 6, 7)]
    [InlineData(13, 6, 13)]
    [InlineData(14, 6, 19)]
    [InlineData(27, 6, 31)]
    [InlineData(27, 8, 33)]
    public void Snap_up_preserves_authored_duration(
        int requested,
        int grid,
        int expected)
    {
        Assert.Equal(expected, StaticGeneratedFrameGrid.SnapUp(requested, grid));
    }

    [Theory]
    [InlineData(new[] { 1, 1 }, 1)]
    [InlineData(new[] { 4, 8 }, 8)]
    [InlineData(new[] { 6, 8 }, 24)]
    [InlineData(new[] { 4, 6, 8 }, 24)]
    public void Compatible_grid_is_the_least_common_multiple(
        int[] grids,
        int expected)
    {
        Assert.Equal(expected, StaticGeneratedFrameGrid.CompatibleGrid(grids));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Grid_must_be_positive(int grid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StaticGeneratedFrameGrid.SnapDown(16, grid));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StaticGeneratedFrameGrid.SnapUp(16, grid));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StaticGeneratedFrameGrid.CompatibleGrid([4, grid]));
    }
}
