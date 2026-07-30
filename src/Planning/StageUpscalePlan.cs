namespace VideoStages.Planning;

/// <summary>The normalized upscale instruction authored for a stage.</summary>
internal enum StageUpscaleMode
{
    None,
    Pixel,
    Model,
    Latent,
    LatentModel,
    Unsupported,
}

internal sealed record StageUpscalePlan(
    StageUpscaleMode Mode,
    double Factor,
    string RawMethod,
    string MethodName);

/// <summary>
/// Classifies the persisted upscale method once, before architecture-specific validation and
/// execution decide which modes they support.
/// </summary>
internal static class StageUpscalePlanCompiler
{
    internal static StageUpscalePlan Compile(StageSpec stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        StageUpscaleMode mode;
        if (stage.Upscale == 1)
        {
            mode = StageUpscaleMode.None;
        }
        else if (stage.IsPixelUpscale)
        {
            mode = StageUpscaleMode.Pixel;
        }
        else if (stage.IsModelUpscale)
        {
            mode = StageUpscaleMode.Model;
        }
        else if (stage.IsLatentUpscale)
        {
            mode = StageUpscaleMode.Latent;
        }
        else if (stage.IsLatentModelUpscale)
        {
            mode = StageUpscaleMode.LatentModel;
        }
        else
        {
            mode = StageUpscaleMode.Unsupported;
        }

        string raw = stage.UpscaleMethod?.Trim() ?? "";
        int separator = raw.IndexOf('-');
        string methodName = separator >= 0 && separator < raw.Length - 1
            ? raw[(separator + 1)..]
            : raw;
        return new(mode, stage.Upscale, raw, methodName);
    }
}
