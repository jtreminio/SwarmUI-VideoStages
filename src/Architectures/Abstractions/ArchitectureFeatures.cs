namespace VideoStages.Architectures.Abstractions;

internal enum ArchitectureEntryMode
{
    TextToVideo,
    ImageToVideo,
    InitVideo,
}

/// <summary>
/// Extras an architecture declares beyond the baseline every video model gets. Audio sourcing is
/// not here: <see cref="VideoArchitectureDescriptor.AudioSourceKinds"/> already states it, and
/// control-signal-derived duration is a property of <see cref="IcLora"/> media.
/// </summary>
[Flags]
internal enum ArchitectureFeature
{
    None = 0,
    PromptRelay = 1 << 0,
    FrameReferences = 1 << 1,
    ReferenceFraming = 1 << 2,
    Retake = 1 << 3,
    AudioReuse = 1 << 5,
    AudioDerivedDuration = 1 << 6,
    IcLora = 1 << 7,
    AudioBoundaryCarry = 1 << 8,
    LatentUpscale = 1 << 9,
    LatentModelUpscale = 1 << 10,
    ClipReferences = 1 << 11,
    StageReferenceStrengths = 1 << 12,
}

/// <summary>The frame positions a model's native image-conditioning path accepts.</summary>
internal enum FrameReferencePosition
{
    First,
    Last,
    Any,
}
