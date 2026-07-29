using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

/// <summary>
/// The one allocation map for VideoStages' stable dynamic node ids. Each allocator owns a
/// contiguous, declared block; <see cref="Id"/> rejects a slot outside its block, so two
/// allocators can never silently drift into the same generated id.
/// </summary>
internal static class StableNodeIds
{
    internal readonly record struct Block(string Name, int Base, int Width)
    {
        internal int EndExclusive => Base + Width;
    }

    /// <summary>Per-stage intermediate save nodes, offset by stage ordinal.</summary>
    internal static Block IntermediateStageSave { get; } = new("intermediate-stage-save", 52100, 100);

    /// <summary>The one assembled-timeline save emitted before frame interpolation.</summary>
    internal static Block PreInterpolationSave { get; } = new("pre-interpolation-save", 52900, 100);

    /// <summary>The one fallback final save node.</summary>
    internal static Block FinalSave { get; } = new("final-save", 52200, 100);

    /// <summary>
    /// Root and clip audio injection. Fixed sub-slots plus a per-clip preserve-window block
    /// starting at slot 400.
    /// </summary>
    internal static Block AudioInjection { get; } = new("audio-injection", 52300, 500);

    /// <summary>Per-stage audio/video window mask nodes, offset by stage ordinal.</summary>
    internal static Block AudioWindowMask { get; } = new("audio-window-mask", 52800, 100);

    internal static IReadOnlyList<Block> All { get; } = [
        IntermediateStageSave,
        FinalSave,
        AudioInjection,
        AudioWindowMask,
        PreInterpolationSave,
    ];

    internal static string Id(WorkflowGenerator g, Block block, int slot = 0)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (slot < 0 || slot >= block.Width)
        {
            throw new InvalidOperationException(
                $"Stable node id slot {slot} is outside the '{block.Name}' block "
                + $"[{block.Base}, {block.EndExclusive}).");
        }
        return g.GetStableDynamicID(block.Base + slot, 0);
    }
}
