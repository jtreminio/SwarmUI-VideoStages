namespace VideoStages.Authoring;

public sealed record LoraRef(
    string Name,
    double Weight = 1.0,
    double? TencWeight = null
);

/// <summary>Frame counts are converted from authored seconds. Architectures own how strength is applied.</summary>
public sealed record RetakeWindowSpec(
    int StartFrame,
    int LengthFrames,
    double Strength
)
{
    public static (int Start, int End) ClampFrameWindow(int startFrame, int lengthFrames, int limitFrames)
    {
        int start = Math.Clamp(startFrame, 0, limitFrames);
        long requestedEnd = (long)startFrame + Math.Max(0, lengthFrames);
        int end = (int)Math.Clamp(requestedEnd, start, (long)limitFrames);
        return (start, end);
    }
}

public sealed record StageSpec(
    int Id,
    double Control,
    double Upscale,
    string UpscaleMethod,
    string Model,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler,
    string ImageReference,
    int ClipStageIndex = 0,
    int ClipStageRawIndex = 0,
    double? ControlNetStrength = null,
    IReadOnlyList<double> IcLoraStrengths = null,
    IReadOnlyList<double> FrameRefStrengths = null,
    bool ImageRefWasExplicit = false,
    RetakeWindowSpec RetakeWindow = null,
    CharacterRefineSpec CharacterRefine = null,
    IReadOnlyList<LoraRef> Loras = null,
    double H3AttentionWindowSeconds = 0,
    string ModelAttentionBackend = null,
    string UseSparseAttention = null
);

public sealed record FrameRefSpec(
    string Source,
    int Frame,
    bool FromEnd,
    string UploadFileName,
    string Data = null
)
{
    internal bool IsOpeningFrame => !FromEnd && Frame == 1;
}

public sealed record UploadedMediaSpec(
    string Data,
    string FileName
);

public enum ClipReferenceKind
{
    Image,
    Video,
    Audio,
}

/// <summary>Whole-clip conditioning media. Source selects an upload, ControlNet input, host capture, previous clip, or audio track. LengthSeconds 0 uses the whole source.</summary>
public sealed record ClipReferenceSpec(
    ClipReferenceKind Kind,
    string Source,
    UploadedMediaSpec Media,
    bool IncludeSoundtrack = false,
    double MediaScale = ReferenceScale.Full,
    double StartSeconds = 0,
    double LengthSeconds = 0,
    string Id = null
);

/// <summary>None applies only the model patch; Visual and Audio consume the corresponding drive stream.</summary>
public enum IcLoraDriveData
{
    None,
    Visual,
    Audio,
}

/// <summary>Source selects an upload or the previous clip. The clip duration owns the range length.</summary>
public sealed record InitVideoSpec(
    string Data,
    string FileName,
    double StartSeconds,
    string Source = MediaSource.Upload,
    int? Frames = null
);

/// <summary>Stage -1 applies to every stage. [AUTO] resolves preset weights; presets do not define the media contract. DriveMedia is independent of the clip audio.</summary>
public sealed record IcLoraSpec(
    string Lora,
    string DriveSource,
    double Strength,
    double AttentionStrength,
    string ControlType,
    UploadedMediaSpec DriveMedia,
    IcLoraDriveData DriveData = IcLoraDriveData.None,
    string Preset = null,
    int Stage = -1,
    IReadOnlyList<ClipReferenceKind> DriveMediaKinds = null
);

/// <summary>Planning advances SourceStartSeconds at each clip boundary crossed by the span.</summary>
public sealed record TimelineAudioSpanSpec(
    string Id,
    UploadedMediaSpec Source,
    string AceStepFunSource,
    double TimelineStartSeconds,
    double SourceStartSeconds,
    double LengthSeconds,
    double Volume = 1,
    int? FirstClipId = null,
    int? LastClipId = null,
    double? FirstClipOffsetSeconds = null,
    double? LastClipOffsetSeconds = null
);

public sealed record PromptWindowSpec(
    string Prompt,
    double Start,
    double Duration
);

/// <summary>Retains skipped stages so their models still participate in architecture validation.</summary>
public sealed record AuthoredStageModelSpec(
    int RawIndex,
    string Model,
    string ModelProfileId,
    bool Skipped
);

public sealed record ClipSpec(
    int Id,
    int? Frames,
    string AudioSource,
    IReadOnlyList<IcLoraSpec> IcLoras,
    bool SaveAudioTrack,
    bool ClipLengthFromAudio,
    bool ClipLengthFromControlNet,
    bool ReuseAudio,
    UploadedMediaSpec UploadedAudio,
    IReadOnlyList<FrameRefSpec> FrameRefs,
    IReadOnlyList<StageSpec> Stages,
    IReadOnlyList<PromptWindowSpec> PromptWindows = null,
    // The left clip owns its outgoing join; the final clip has none.
    string BoundaryOut = Constants.BoundaryOutCut,
    // Authored outgoing join window in frames; the selected architecture owns its meaning and grid.
    int BoundaryOutOverlap = 0,
    InitVideoSpec InitVideo = null,
    // Overlap-mode joins can preserve the outgoing audio tail as opening generation context.
    bool BoundaryOutCarryAudio = false,
    double BoundaryOutReferenceScale = ReferenceScale.Full,
    bool BoundaryOutReferenceIncludeSoundtrack = true,
    ReferenceFramingMode ReferenceFraming = ReferenceFramingMode.Crop,
    // Whole-clip references with no frame position; only architectures declaring
    // ArchitectureFeature.ClipReferences consume them.
    IReadOnlyList<ClipReferenceSpec> References = null,
    double UploadedAudioStartSeconds = 0,
    double UploadedAudioLengthSeconds = 0,
    MiniMaxTextEncoder H3TextEncoder = MiniMaxTextEncoder.Default,
    MiniMaxReferenceImageSize H3ReferenceImageSize = MiniMaxReferenceImageSize.Match
)
{
    /// <summary>Persisted repair/diagnostic hint. Resolved stage models own behavior.</summary>
    public string AuthoredArchitectureHint { get; init; }

    /// <summary>Persisted repair/diagnostic hint. Resolved stage models own behavior.</summary>
    public string AuthoredModelProfileHint { get; init; }

    public IReadOnlyList<AuthoredStageModelSpec> AuthoredStages { get; init; } = [];

    public bool UseSeedVr { get; init; }

    public bool AudioOnly { get; init; }
}

public sealed record TimelineSpec(
    int Width,
    int Height,
    int FPS,
    bool IsTextToVideo,
    IReadOnlyList<ClipSpec> Clips,
    bool HasConfiguredResolution = true,
    IReadOnlyList<TimelineAudioSpanSpec> TimelineAudioSpans = null)
{
    public LegacyVideoSwapRequestSnapshot LegacyVideoSwap { get; init; } = new();
}

/// <summary>Legacy host video-swap settings are retained for diagnostics only.</summary>
public sealed record LegacyVideoSwapRequestSnapshot(
    string VideoSwapModelName = null,
    bool HasExplicitVideoSwapPercent = false,
    double? ExplicitVideoSwapPercent = null,
    bool HasVideoSwapSectionOverrides = false)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(VideoSwapModelName)
        || HasExplicitVideoSwapPercent
        || HasVideoSwapSectionOverrides;
}
