namespace VideoStages.Authoring;

public sealed record LoraRef(
    string Name,
    double Weight = 1.0,
    double? TencWeight = null
);

/// <summary>
/// Retake window: regenerate only frames <c>[StartFrame, StartFrame + LengthFrames)</c> of the base
/// video, preserving the rest. <c>Strength</c> is the requested regeneration strength inside the
/// window (1.0 = full regeneration); the selected architecture owns how that request is executed.
/// Frame counts are converted from the per-clip seconds window while reading the request.
/// </summary>
public sealed record RetakeWindowSpec(
    int StartFrame,
    int LengthFrames,
    double Strength
)
{
    /// <summary>
    /// Clamps a start/length pixel-frame window to <c>[0, limitFrames]</c> and returns the resulting
    /// <c>[Start, End)</c> span; a non-positive length collapses to an empty span at Start.
    /// </summary>
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
    IReadOnlyList<LoraRef> Loras = null,
    IReadOnlyList<double> LoraWeights = null,
    RetakeWindowSpec RetakeWindow = null
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

/// <summary>
/// One whole-clip reference: media the architecture conditions on without attaching it to any
/// frame position. <c>Source</c> is "Upload" for <c>Media</c>, a ControlNet input for any kind,
/// a host capture name ("Base", "Refiner") for images, or an AceStepFun track for audio.
/// <c>IncludeSoundtrack</c> asks a video reference to also pass its own audio track as the paired
/// reference audio. <c>MediaScale</c> downsamples a video reference before it is presented,
/// trading detail for reference tokens.
/// </summary>
public sealed record ClipReferenceSpec(
    ClipReferenceKind Kind,
    string Source,
    UploadedMediaSpec Media,
    bool IncludeSoundtrack = false,
    double MediaScale = ReferenceScale.Full
);

/// <summary>
/// The one data stream an IC-LoRA consumes from its selected drive source. <c>Visual</c> extracts
/// image/video frames, <c>Audio</c> extracts audio from an audio/video source, and <c>None</c>
/// applies only the model patch.
/// </summary>
public enum IcLoraDriveData
{
    None,
    Visual,
    Audio,
}

/// <summary>
/// Pre-existing footage used as the clip's starting point instead of a from-scratch generation.
/// <c>Data</c> is the uploaded video; <c>StartSeconds</c> is how far into the file the
/// used range begins — its length is the clip's own duration. The backend conforms the range to
/// the timeline (fps resample, frame window, resize) and supplies it as the clip's architecture-owned
/// source input. The architecture decides how each stage consumes or transforms that source.
/// </summary>
public sealed record InitVideoSpec(
    string Data,
    string FileName,
    double StartSeconds
);

/// <summary>
/// One in-context LoRA on a clip. <c>Lora</c> is the LoRA model name, or "[AUTO]" to resolve the
/// selected architecture preset's conventional download path; <c>Preset</c> selects catalog
/// weights and any genuinely preset-specific graph behavior, never the media contract. <c>Strength</c>,
/// <c>AttentionStrength</c>, and <c>ControlType</c> are architecture-interpreted settings.
/// <c>DriveSource</c> selects either the per-entry upload or contextual media entering the stage;
/// <c>DriveData</c> declares the single stream consumed from that source, while
/// <c>DriveMediaKinds</c> optionally narrows the accepted source containers.
/// <c>Stage</c> scopes the entry to one authored stage
/// (-1 = every stage). <c>DriveMedia</c> is the upload used only by the Upload source and is
/// independent from the clip's base audio track.
/// </summary>
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

/// <summary>
/// One authored span of an audio track, positioned on the final multi-clip timeline. Planning projects
/// this interval onto every clip it intersects, advancing <see cref="SourceStartSeconds"/> at each
/// seam so every clip receives the correct source slice.
/// </summary>
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

/// <summary>
/// Architecture-relevant identity from the authored stage list. Unlike <see cref="StageSpec"/>,
/// this projection intentionally retains skipped stages so a persisted mixed-architecture clip
/// cannot evade validation by hiding one of its models.
/// </summary>
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
    IReadOnlyList<LoraRef> Loras = null,
    IReadOnlyList<PromptWindowSpec> PromptWindows = null,
    // Cross-clip continuity at THIS clip's outgoing boundary (clip N -> N+1): "cut" (hard concat),
    // "continue" (architecture-owned generation-time continuity), or "crossfade" (pixel dissolve
    // over an overlap window). Only meaningful for non-final clips in a multi-clip request.
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
    IReadOnlyList<ClipReferenceSpec> References = null
)
{
    /// <summary>Persisted repair/diagnostic hint. Resolved stage models own behavior.</summary>
    public string AuthoredArchitectureHint { get; init; }

    /// <summary>Persisted repair/diagnostic hint. Resolved stage models own behavior.</summary>
    public string AuthoredModelProfileHint { get; init; }

    public IReadOnlyList<AuthoredStageModelSpec> AuthoredStages { get; init; } = [];

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

/// <summary>
/// Immutable authored facts from SwarmUI's legacy request-global video-swap controls. VideoStages
/// keeps these only to explain why they are ignored; execution never consumes them.
/// </summary>
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
