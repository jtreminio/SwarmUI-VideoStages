namespace VideoStages;

public sealed record LoraRef(
    string Name,
    double Weight = 1.0,
    double? TencWeight = null
);

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
    int ClipStageIndex = 0,
    double? ControlNetStrength = null,
    IReadOnlyList<double> ImageRefStrengths = null,
    bool ImageRefWasExplicit = false,
    int? EndStep = null,
    string Prompt = "",
    string NegativePrompt = "",
    IReadOnlyList<LoraRef> Loras = null
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
    string Prompt = "",
    string NegativePrompt = "",
    IReadOnlyList<LoraRef> Loras = null
);

public sealed record VideoStagesSpec(
    int Width,
    int Height,
    int FPS,
    bool IsTextToVideo,
    IReadOnlyList<ClipSpec> Clips
);
