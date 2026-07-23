namespace VideoStages.Architectures.Ltx2.Planning;

internal enum IcLoraDriveMediaConsumption
{
    VisualGuide,
    AudioReference,
}

internal sealed record IcLoraDriveMediaContract(
    IcLoraDriveMediaConsumption Consumption,
    bool RequiresUpload)
{
    internal bool Accepts(IcLoraDriveMediaKind kind) => Consumption switch
    {
        IcLoraDriveMediaConsumption.AudioReference =>
            kind is IcLoraDriveMediaKind.Audio or IcLoraDriveMediaKind.Video,
        _ => kind is IcLoraDriveMediaKind.Image or IcLoraDriveMediaKind.Video,
    };
}

/// <summary>
/// LTX-specific interpretation of an IC-LoRA's single Drive Media field. Unknown/custom presets
/// retain the normal visual-guide behavior; only named architecture presets opt into other streams.
/// </summary>
internal static class IcLoraDriveMediaContracts
{
    internal const string LipDubPreset = "lipdub";

    private static readonly IcLoraDriveMediaContract VisualGuide = new(
        IcLoraDriveMediaConsumption.VisualGuide,
        RequiresUpload: false);

    private static readonly IcLoraDriveMediaContract AudioReference = new(
        IcLoraDriveMediaConsumption.AudioReference,
        RequiresUpload: true);

    internal static IcLoraDriveMediaContract Resolve(string preset) =>
        StringUtils.Equals(preset, LipDubPreset)
            ? AudioReference
            : VisualGuide;
}
