using VideoStages.Authoring;

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
    private static bool HasMethodName(string method, string prefix) =>
        method.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(method[prefix.Length..]);

    internal static StageUpscaleMode Classify(string method)
    {
        string normalized = method?.Trim() ?? "";
        if (HasMethodName(normalized, "latentmodel-"))
        {
            return StageUpscaleMode.LatentModel;
        }
        if (HasMethodName(normalized, "latent-"))
        {
            return StageUpscaleMode.Latent;
        }
        if (HasMethodName(normalized, "pixel-"))
        {
            return StageUpscaleMode.Pixel;
        }
        if (HasMethodName(normalized, "model-"))
        {
            return StageUpscaleMode.Model;
        }
        return StageUpscaleMode.Unsupported;
    }

    internal static StageUpscalePlan Compile(StageSpec stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        string raw = stage.UpscaleMethod?.Trim() ?? "";
        StageUpscaleMode mode = stage.Upscale == 1
            ? StageUpscaleMode.None
            : Classify(raw);
        int separator = raw.IndexOf('-');
        string methodName = separator >= 0 && separator < raw.Length - 1
            ? raw[(separator + 1)..].Trim()
            : raw;
        return new(mode, stage.Upscale, raw, methodName);
    }
}
