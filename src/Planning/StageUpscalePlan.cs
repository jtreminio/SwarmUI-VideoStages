using VideoStages.Authoring;

namespace VideoStages.Planning;

internal enum StageUpscaleMode
{
    None,
    Pixel,
    Model,
    Latent,
    LatentModel,
    Unsupported,
}

/// <summary>The normalized upscale instruction authored for a stage.</summary>
internal sealed record StageUpscalePlan(
    StageUpscaleMode Mode,
    double Factor,
    string RawMethod,
    string MethodName);

/// <summary>
/// Classifies the persisted upscale method; architectures decide which modes they support.
/// </summary>
internal static class StageUpscalePlanCompiler
{
    // Tried in order, so "latentmodel-" has to precede the "latent-" it starts with.
    private static readonly (string Prefix, StageUpscaleMode Mode)[] MethodPrefixes =
    [
        ("latentmodel-", StageUpscaleMode.LatentModel),
        ("latent-", StageUpscaleMode.Latent),
        ("pixel-", StageUpscaleMode.Pixel),
        ("model-", StageUpscaleMode.Model),
    ];

    internal static StageUpscaleMode Classify(string method)
    {
        string normalized = method?.Trim() ?? "";
        foreach ((string prefix, StageUpscaleMode mode) in MethodPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(normalized[prefix.Length..]))
            {
                return mode;
            }
        }
        return StageUpscaleMode.Unsupported;
    }

    internal static string WireName(StageUpscaleMode mode) => mode switch
    {
        StageUpscaleMode.Pixel => "pixel",
        StageUpscaleMode.Model => "model",
        StageUpscaleMode.Latent => "latent",
        StageUpscaleMode.LatentModel => "latent-model",
        StageUpscaleMode.Unsupported => "unsupported",
        // None means "no upscale authored", which the frontend expresses as a factor of 1.
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>A factor of 1 upscales nothing, whatever method is persisted.</summary>
    internal static StageUpscaleMode Mode(StageSpec stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Upscale == 1
            ? StageUpscaleMode.None
            : Classify(stage.UpscaleMethod);
    }

    internal static StageUpscalePlan Compile(StageSpec stage)
    {
        StageUpscaleMode mode = Mode(stage);
        string raw = stage.UpscaleMethod?.Trim() ?? "";
        int separator = raw.IndexOf('-');
        string methodName = separator >= 0 && separator < raw.Length - 1
            ? raw[(separator + 1)..].Trim()
            : raw;
        return new(mode, stage.Upscale, raw, methodName);
    }

    /// <summary>
    /// Renders the checked-in TypeScript projection. A backend test compares this byte for byte
    /// with <c>frontend/generatedUpscaleModes.ts</c>. Only the table crosses; the match rule
    /// itself is written in both languages and pinned by upscale-method-cases.json.
    /// </summary>
    internal static string RenderGeneratedTypeScript()
    {
        StringBuilder result = new();
        void Line(string value = "") => result.Append(value).Append('\n');

        Line("// Generated from StageUpscalePlan.cs. Do not edit by hand.");
        Line();
        Line("/** The mode a method with no recognized prefix classifies as. */");
        Line("export const UPSCALE_MODE_UNSUPPORTED ="
            + $" \"{WireName(StageUpscaleMode.Unsupported)}\";");
        Line();
        Line("/** Method prefix to mode, in the order the classifier tries them. */");
        Line("export const UPSCALE_METHOD_PREFIXES = [");
        foreach ((string prefix, StageUpscaleMode mode) in MethodPrefixes)
        {
            Line($"    [\"{prefix}\", \"{WireName(mode)}\"],");
        }
        Line("] as const;");

        return result.ToString();
    }
}
