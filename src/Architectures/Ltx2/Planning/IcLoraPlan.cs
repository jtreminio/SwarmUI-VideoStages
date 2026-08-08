using VideoStages.Authoring;

namespace VideoStages.Architectures.Ltx2.Planning;

internal enum IcLoraMediaSourceKind
{
    Upload,
    Incoming,
    ControlNet,
    Unknown,
}

internal enum IcLoraControlMode
{
    None,
    Canny,
    Depth,
    Normal,
    Unknown,
}

internal enum IcLoraDriveMediaKind
{
    None,
    Image,
    Video,
    Audio,
    Unknown,
}

internal sealed record IcLoraDrivePlan(
    IcLoraDriveData Stream,
    IcLoraMediaSourceKind Source,
    IcLoraDriveMediaKind MediaKind,
    UploadedMediaSpec Upload,
    int? ControlNetIndex);

internal sealed record IcLoraPlan(
    int EntryIndex,
    string ModelName,
    bool UsesAutoModel,
    string Preset,
    double ModelStrength,
    double AttentionStrength,
    IcLoraControlMode ControlMode,
    IcLoraDrivePlan Drive,
    int DimensionDownscaleFactor,
    double? GuideStrength)
{
    internal bool HasVisualGuide => Drive.Stream == IcLoraDriveData.Visual;

    internal bool HasAudioReference => Drive.Stream == IcLoraDriveData.Audio;
}
