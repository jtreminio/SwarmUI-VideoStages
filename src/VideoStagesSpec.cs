namespace VideoStages;

public sealed record StageSpec(
    int Id,
    double Control,
    double Upscale,
    string UpscaleMethod,
    string Model,
    string Vae,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler,
    string ImageReference,
    bool Skipped = false,
    int ClipId = 0,
    string ClipAudioSource = null,
    bool ClipLengthFromAudio = false,
    bool ClipLengthFromControlNet = false,
    int ClipWidth = 0,
    int ClipHeight = 0,
    int? ClipFrames = null,
    int ClipFPS = 0,
    bool ClipReuseAudio = false,
    string ClipControlNetSource = null,
    string ClipControlNetLora = null,
    int ClipStageIndex = 0,
    int ClipStageCount = 0,
    double? ControlNetStrength = null,
    IReadOnlyList<ImageRefSpec> ClipRefs = null,
    IReadOnlyList<double> RefStrengths = null,
    bool ImageReferenceWasExplicit = false,
    int? EndStep = null,
    bool IsTextToVideo = false
);

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

public sealed record ClipSpec(
    int Id,
    bool Skipped,
    double DurationSeconds,
    string AudioSource,
    string ControlNetSource,
    string ControlNetLora,
    bool SaveAudioTrack,
    bool ClipLengthFromAudio,
    bool ClipLengthFromControlNet,
    bool ReuseAudio,
    int? Width,
    int? Height,
    UploadedAudioSpec UploadedAudio,
    IReadOnlyList<ImageRefSpec> Refs,
    IReadOnlyList<StageSpec> Stages
);

public sealed record VideoStagesSpec(
    int Width,
    int Height,
    int FPS,
    bool IsTextToVideo,
    IReadOnlyList<ClipSpec> Clips
);

public sealed record ClipWithStages(
    ClipSpec Clip,
    IReadOnlyList<StageSpec> Stages
);
