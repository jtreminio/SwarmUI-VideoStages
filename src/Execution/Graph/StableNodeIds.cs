using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Execution.Graph;

internal static class StableNodeIds
{
    /// <summary>Per-stage intermediate save nodes, offset by stage ordinal.</summary>
    internal const int IntermediateStageSave = 52100;

    /// <summary>The one merged-timeline save emitted before frame interpolation.</summary>
    internal const int PreInterpolationSave = 52900;

    /// <summary>The one fallback final save node.</summary>
    internal const int FinalSave = 52200;

    /// <summary>
    /// Root and clip audio injection. Fixed sub-slots plus a per-clip preserve-window block
    /// starting at slot 400.
    /// </summary>
    internal const int AudioInjection = 52300;

    /// <summary>Per-stage audio/video window mask nodes, offset by stage ordinal.</summary>
    internal const int AudioWindowMask = 52800;

    internal static string Id(WorkflowGenerator g, int baseId, int slot = 0)
    {
        ArgumentNullException.ThrowIfNull(g);
        return g.GetStableDynamicID(baseId + slot, 0);
    }
}
