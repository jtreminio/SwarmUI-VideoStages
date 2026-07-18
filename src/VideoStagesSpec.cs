namespace VideoStages;

public sealed record LoraRef(
    string Name,
    double Weight = 1.0,
    double? TencWeight = null
);

/// <summary>
/// Retake window: regenerate only frames <c>[StartFrame, StartFrame + LengthFrames)</c> of the base
/// video, preserving the rest; <c>Strength</c> is the per-frame noise-mask value inside it (1.0 = full
/// regen). Presence on a stage enables retake: the stage encodes the whole base to latent, attaches a
/// noise mask windowed to this span, and samples from step 0 so the mask (not <c>StartStep</c>) governs.
/// Frame counts are converted from the per-clip seconds window at parse time using the timeline fps.
/// </summary>
public sealed record RetakeWindowSpec(
    int StartFrame,
    int LengthFrames,
    double Strength
);

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
    double? ControlNetStrength = null,
    IReadOnlyList<double> ImageRefStrengths = null,
    bool ImageRefWasExplicit = false,
    IReadOnlyList<LoraRef> Loras = null,
    RetakeWindowSpec RetakeWindow = null
)
{
    public bool IsLatentModelUpscale => HasUpscaleMethodPrefix("latentmodel-");
    public bool IsLatentUpscale => HasUpscaleMethodPrefix("latent-");
    public bool IsPixelUpscale => HasUpscaleMethodPrefix("pixel-");
    public bool IsModelUpscale => HasUpscaleMethodPrefix("model-");

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

public sealed record UploadedAudioSpec(
    string Data,
    string FileName
);

/// <summary>
/// One overlay audio piece on a clip, in addition to its base audio source.
/// <c>Source</c> is an uploaded audio blob; <c>StartSeconds</c> is where the piece begins inside the
/// clip; <c>TrimStartSeconds</c> is how far into the source file playback starts; <c>LengthSeconds</c> is
/// how long it plays. All seconds, clamped inside the clip at parse time. The backend mixes each segment
/// additively over the base audio (AudioMerge, merge_method="add").
/// </summary>
public sealed record AudioSegmentSpec(
    UploadedAudioSpec Source,
    double StartSeconds,
    double TrimStartSeconds,
    double LengthSeconds,
    string AceStepFunSource = null
);

public sealed record PromptWindowSpec(
    string Prompt,
    double Start,
    double Duration
);

public sealed record ClipSpec(
    int Id,
    int? Frames,
    string AudioSource,
    string ControlNetSource,
    string ControlNetLora,
    bool SaveAudioTrack,
    bool ClipLengthFromAudio,
    bool ClipLengthFromControlNet,
    bool ReuseAudio,
    UploadedAudioSpec UploadedAudio,
    IReadOnlyList<ImageRefSpec> ImageRefs,
    IReadOnlyList<StageSpec> Stages,
    IReadOnlyList<AudioSegmentSpec> AudioSegments = null,
    IReadOnlyList<LoraRef> Loras = null,
    IReadOnlyList<PromptWindowSpec> PromptWindows = null,
    // Cross-clip continuity at THIS clip's outgoing boundary (clip N -> N+1): "cut" (hard concat),
    // "continue" (generation-time continuity: the next clip generates from this clip's final frame, and
    // the merge collapses the duplicated seam frame), or "crossfade" (pixel dissolve over an overlap
    // window). Only meaningful for non-final clips in a parallel multi-clip run.
    string BoundaryOut = Constants.BoundaryOutCut
);

public sealed record VideoStagesSpec(
    int Width,
    int Height,
    int FPS,
    bool IsTextToVideo,
    IReadOnlyList<ClipSpec> Clips
);
