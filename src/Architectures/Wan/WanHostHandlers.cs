namespace VideoStages.Architectures.Wan;

/// <summary>Registers the narrowly scoped host callbacks required by WAN timelines.</summary>
internal static class WanHostHandlers
{
    internal static void Register()
    {
        WanLegacySwapIsolation.RegisterHandlers();
        WanVideoEndFrameIsolation.RegisterHandlers();
    }
}
