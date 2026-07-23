namespace VideoStages;

public sealed record LoraRef(
    string Name,
    double Weight = 1.0,
    double? TencWeight = null
);

/// <summary>
/// Retake window: regenerate only frames <c>[StartFrame, StartFrame + LengthFrames)</c> of the base
/// video, preserving the rest. <c>Strength</c> is the requested regeneration strength inside the
/// window (1.0 = full regeneration); the selected architecture owns how that request is executed.
/// Frame counts are converted from the per-clip seconds window at parse time using the timeline fps.
/// </summary>
public sealed record RetakeWindowSpec(
    int StartFrame,
    int LengthFrames,
    double Strength
)
{
    /// <summary>
    /// Clamps a start/length pixel-frame window to <c>[0, limitFrames]</c> and returns the resulting
    /// <c>[Start, End)</c> span; a non-positive length collapses to an empty span at Start. Shared by the
    /// audio and video retake maskers so both windows describe the same frames.
    /// </summary>
    public static (int Start, int End) ClampFrameWindow(int startFrame, int lengthFrames, int limitFrames)
    {
        int start = Math.Clamp(startFrame, 0, limitFrames);
        int end = Math.Clamp(startFrame + Math.Max(0, lengthFrames), start, limitFrames);
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
    IReadOnlyList<double> ImageRefStrengths = null,
    bool ImageRefWasExplicit = false,
    IReadOnlyList<LoraRef> Loras = null,
    RetakeWindowSpec RetakeWindow = null
)
{
    // Intentional read-only workflow projections: the upscale-method prefix dispatch below encodes
    // generation policy but is colocated with the data it reads, by design (no behavior, no host state).
    public bool IsLatentModelUpscale => HasUpscaleMethodPrefix("latentmodel-");
    public bool IsLatentUpscale => HasUpscaleMethodPrefix("latent-");
    public bool IsPixelUpscale => HasUpscaleMethodPrefix("pixel-");
    public bool IsModelUpscale => HasUpscaleMethodPrefix("model-");

    /// <summary>
    /// True when the authored stage requests no generation or architecture-owned latent transform.
    /// Retakes and latent scaling remain active work even when Control is zero.
    /// </summary>
    public bool IsPassthrough => Control <= 0
        && RetakeWindow is null
        && !(Upscale != 1 && (IsLatentUpscale || IsLatentModelUpscale));

    private bool HasUpscaleMethodPrefix(string prefix) =>
        UpscaleMethod?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false;
}

public sealed record ImageRefSpec(
    string Source,
    int Frame,
    bool FromEnd,
    string UploadFileName,
    string Data = null
);

/// <summary>An embedded upload used by audio, video, image, or architecture-specific media fields.</summary>
public sealed record UploadedMediaSpec(
    string Data,
    string FileName
);

/// <summary>
/// Pre-existing footage used as the clip's starting point instead of a from-scratch generation.
/// <c>Data</c> is the uploaded video; <c>StartSeconds</c> is how far into the file the
/// used range begins — its length is the clip's own duration. The backend conforms the range to
/// the timeline (fps resample, frame window, resize) and supplies it as the clip's architecture-owned
/// source input. The architecture decides how each stage consumes or transforms that source.
/// </summary>
public sealed record SourceVideoSpec(
    string Data,
    string FileName,
    double StartSeconds
);

/// <summary>
/// One in-context LoRA on a clip. <c>Lora</c> is the LoRA model name, or "[AUTO]" to resolve the
/// selected architecture preset's conventional download path; <c>Preset</c> is the frontend
/// catalog id and may select an architecture-owned media contract. <c>Strength</c>,
/// <c>AttentionStrength</c>, and <c>ControlType</c> are architecture-interpreted settings.
/// <c>Source</c> identifies an authored visual-guide source where the selected contract uses one;
/// <c>Stage</c> scopes the entry to one authored stage (-1 = every stage). <c>DriveMedia</c> is a
/// single upload whose usable streams are selected by the architecture contract. It is independent
/// from the clip's base audio track.
/// </summary>
public sealed record IcLoraSpec(
    string Lora,
    string Source,
    double Strength,
    double AttentionStrength,
    string ControlType,
    UploadedMediaSpec DriveMedia,
    string Preset = null,
    int Stage = -1
);

/// <summary>
/// One overlay audio piece on a clip, in addition to its base audio source.
/// <c>Source</c> is an uploaded audio blob; <c>StartSeconds</c> is where the piece begins inside the
/// clip; <c>TrimStartSeconds</c> is how far into the source file playback starts; <c>LengthSeconds</c> is
/// how long it plays; and <c>Volume</c> is its relative loudness before mixing: 1 is unchanged,
/// lower is quieter, and higher is louder. All values are normalized at parse time. The runtime
/// mixes each segment additively over the base audio.
/// </summary>
public sealed record AudioSegmentSpec(
    UploadedMediaSpec Source,
    double StartSeconds,
    double TrimStartSeconds,
    double LengthSeconds,
    string AceStepFunSource = null,
    double Volume = 1
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
    IReadOnlyList<ImageRefSpec> ImageRefs,
    IReadOnlyList<StageSpec> Stages,
    IReadOnlyList<AudioSegmentSpec> AudioSegments = null,
    IReadOnlyList<LoraRef> Loras = null,
    IReadOnlyList<PromptWindowSpec> PromptWindows = null,
    // Cross-clip continuity at THIS clip's outgoing boundary (clip N -> N+1): "cut" (hard concat),
    // "continue" (architecture-owned generation-time continuity), or "crossfade" (pixel dissolve
    // over an overlap window). Only meaningful for non-final clips in a parallel multi-clip run.
    string BoundaryOut = Constants.BoundaryOutCut,
    // Authored boundary overlap in frames; the selected architecture normalizes its own grid:
    // for "continue" the frozen-context length (window = overlap+1), for "crossfade" the requested
    // dissolve length; ignored for "cut".
    int BoundaryOutOverlap = 0,
    SourceVideoSpec SourceVideo = null,
    // When true on a non-cut boundary, the next generated clip receives the outgoing audio tail
    // as preserved opening context and generates the continuation after that window.
    bool BoundaryOutCarryAudio = false
)
{
    public string AuthoredArchitectureId { get; init; }

    public string AuthoredModelProfileId { get; init; }

    public IReadOnlyList<AuthoredStageModelSpec> AuthoredStages { get; init; } = [];

}

public sealed record VideoStagesSpec(
    int Width,
    int Height,
    int FPS,
    bool IsTextToVideo,
    IReadOnlyList<ClipSpec> Clips,
    bool HasConfiguredResolution = true
);
