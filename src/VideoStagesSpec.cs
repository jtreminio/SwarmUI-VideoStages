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
/// One in-context LoRA on a clip. <c>Lora</c> is the LoRA model name, or "[AUTO]" to resolve the
/// preset's conventional download path (IcLoraWeights.ModelNameFor(<c>Preset</c>)); <c>Preset</c> is
/// the frontend catalog id — otherwise editor guidance only; <c>Strength</c> is the LoRA model
/// strength (loader <c>strength_model</c>); <c>AttentionStrength</c> below 1.0 selects the Advanced guide
/// node for per-guide self-attention influence; <c>ControlType</c> optionally renders the drive video
/// into a control signal (canny/depth/normal) before guiding; <c>Source</c> is where the drive video
/// comes from — "Upload" (embedded <c>Video</c> blob), "Stage Input" (the frames entering the target
/// stage), or a captured core "ControlNet N" branch. <c>Stage</c> restricts the entry to one stage
/// (-1 = every stage), indexed by the clip's authored stage list (StageSpec.ClipStageRawIndex).
/// An entry with no drive video still applies the LoRA to the model (loader-only; e.g. HDR,
/// text-driven use).
/// The guide's latent_downscale_factor is wired from the loader's safetensors-metadata output.
/// <c>DriveAudioRef</c> uses the uploaded drive video's own audio track as the clip's
/// voice-reference sample (LTXVSetAudioRefTokens) — the official LipDub one-file flow.
/// </summary>
public sealed record IcLoraSpec(
    string Lora,
    string Source,
    double Strength,
    double AttentionStrength,
    string ControlType,
    UploadedAudioSpec Video,
    string Preset = null,
    int Stage = -1,
    bool DriveAudioRef = false
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
    IReadOnlyList<IcLoraSpec> IcLoras,
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
)
{
    /// <summary>First IC-LoRA entry driven by a captured core "ControlNet N" branch, or null. That
    /// entry's slot doubles as the clip's audio-capture and clip-length-from-controlnet source.</summary>
    public IcLoraSpec PrimarySlotEntry => IcLoras?.FirstOrDefault(
        entry => !StringUtils.Equals(entry.Source, Constants.IcLoraSourceUpload)
            && !StringUtils.Equals(entry.Source, Constants.IcLoraSourceStageInput));

    public bool HasIcLoras => IcLoras is { Count: > 0 };

    /// <summary>First IC-LoRA entry whose uploaded drive video doubles as the clip's
    /// voice-reference sample, or null. Takes precedence over a clip-level
    /// "Voice Reference" audio upload (the official LipDub one-file flow).</summary>
    public IcLoraSpec VoiceRefDriveEntry => IcLoras?.FirstOrDefault(
        entry => entry.DriveAudioRef
            && StringUtils.Equals(entry.Source, Constants.IcLoraSourceUpload)
            && !string.IsNullOrWhiteSpace(entry.Video?.Data));

    /// <summary>True when this clip conditions audio generation on a voice-reference sample
    /// (per-entry drive audio or clip-level "Voice Reference" upload) instead of locking a track.</summary>
    public bool UsesVoiceRefAudio => VoiceRefDriveEntry is not null
        || StringUtils.Equals(AudioSource, Constants.AudioSourceVoiceRef);
}

public sealed record VideoStagesSpec(
    int Width,
    int Height,
    int FPS,
    bool IsTextToVideo,
    IReadOnlyList<ClipSpec> Clips
);
