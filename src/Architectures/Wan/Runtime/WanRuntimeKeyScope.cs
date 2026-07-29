namespace VideoStages.Architectures.Wan;

/// <summary>Formats the request-local runtime keys owned by the Wan adapter.</summary>
internal sealed class WanRuntimeKeyScope
{
    private static string Prefix { get; } =
        $"videostages.arch.{WanArchitectureModule.ArchitectureId}";

    internal string PreCoreMedia => $"{Prefix}.pre-core.media";

    internal string PreCoreVae => $"{Prefix}.pre-core.vae";

    internal string PreCoreNodeIds => $"{Prefix}.pre-core-node-ids";
}
