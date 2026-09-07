namespace VideoStages.Execution.Graph;

/// <summary>
/// Reserved id blocks this extension hands to <c>GetStableDynamicID</c>, which offsets them by
/// 1000 and probes upward for a free id. One table so blocks claimed by different subsystems
/// cannot silently overlap.
/// </summary>
internal static class StableNodeIds
{
    /// <summary>Per-stage intermediate save nodes, offset by stage ordinal.</summary>
    internal const int IntermediateStageSave = 52100;

    /// <summary>The one merged-timeline save emitted before frame interpolation.</summary>
    internal const int PreInterpolationSave = 52900;

    /// <summary>Per-clip audio-only saves when the next clip does not consume the result.</summary>
    internal const int AudioOnlyClipSave = 53000;

    /// <summary>
    /// Root and clip audio injection. Fixed sub-slots plus a per-clip preserve-window block
    /// starting at slot 400.
    /// </summary>
    internal const int AudioInjection = 52300;

    /// <summary>Per-stage audio/video window mask nodes, offset by stage ordinal.</summary>
    internal const int AudioWindowMask = 52800;
}
