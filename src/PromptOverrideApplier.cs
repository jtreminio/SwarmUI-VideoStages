using System.Globalization;
using Newtonsoft.Json.Linq;

namespace VideoStages;

internal static class PromptOverrideApplier
{
    private enum OverrideKind { String, Int, Double, Bool }

    private static readonly Dictionary<string, (string Canonical, OverrideKind Kind)> ClipFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["duration"] = ("duration", OverrideKind.Double),
            ["audiosource"] = ("audioSource", OverrideKind.String),
            ["saveaudiotrack"] = ("saveAudioTrack", OverrideKind.Bool),
            ["cliplengthfromaudio"] = ("clipLengthFromAudio", OverrideKind.Bool),
            ["cliplengthfromcontrolnet"] = ("clipLengthFromControlNet", OverrideKind.Bool),
            ["reuseaudio"] = ("reuseAudio", OverrideKind.Bool),
            ["boundaryout"] = ("boundaryOut", OverrideKind.String),
            ["boundaryoutoverlap"] = ("boundaryOutOverlap", OverrideKind.Int),
            ["skipped"] = ("skipped", OverrideKind.Bool),
        };

    private static readonly Dictionary<string, (string Canonical, OverrideKind Kind)> StageFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = ("model", OverrideKind.String),
            ["steps"] = ("steps", OverrideKind.Int),
            ["cfgscale"] = ("cfgScale", OverrideKind.Double),
            ["control"] = ("control", OverrideKind.Double),
            ["upscale"] = ("upscale", OverrideKind.Double),
            ["upscalemethod"] = ("upscaleMethod", OverrideKind.String),
            ["sampler"] = ("sampler", OverrideKind.String),
            ["scheduler"] = ("scheduler", OverrideKind.String),
            ["imagereference"] = ("imageReference", OverrideKind.String),
            ["controlnetstrength"] = ("controlNetStrength", OverrideKind.Double),
            ["skipped"] = ("skipped", OverrideKind.Bool),
        };

    public static (int? Width, int? Height, int? FPS) ApplyTopLevel(
        PromptParser.VideoStageTagData tags,
        int? width,
        int? height,
        int? fps,
        Action<string> warn = null)
    {
        foreach ((string field, string value) in tags.TopLevelOverrides)
        {
            if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            {
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: ignoring invalid top-level override '{field}' = '{value}'.");
                continue;
            }
            if (string.Equals(field, "width", StringComparison.OrdinalIgnoreCase)) { width = parsed; }
            else if (string.Equals(field, "height", StringComparison.OrdinalIgnoreCase)) { height = parsed; }
            else if (string.Equals(field, "fps", StringComparison.OrdinalIgnoreCase)) { fps = parsed; }
        }
        return (width, height, fps);
    }

    public static void ApplyClipAndStage(
        List<JObject> rawEntries,
        PromptParser.VideoStageTagData tags,
        Action<string> warn = null)
    {
        foreach ((int clipIndex, List<(string Field, string Value)> overrides) in tags.ClipOverrides)
        {
            if (clipIndex < 0 || clipIndex >= rawEntries.Count)
            {
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: clip override targets out-of-range clip {clipIndex}; ignoring.");
                continue;
            }
            foreach ((string field, string value) in overrides)
            {
                ApplyScalar(
                    rawEntries[clipIndex],
                    field,
                    value,
                    ClipFields,
                    $"clip {clipIndex}",
                    warn);
            }
        }
        foreach (((int clipIndex, int stageIndex), List<(string Field, string Value)> overrides) in tags.StageOverrides)
        {
            if (clipIndex < 0 || clipIndex >= rawEntries.Count)
            {
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: stage override targets out-of-range clip {clipIndex}; ignoring.");
                continue;
            }
            JObject stage = GetStage(rawEntries[clipIndex], stageIndex);
            if (stage is null)
            {
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: stage override targets out-of-range stage {stageIndex} on clip {clipIndex}; ignoring.");
                continue;
            }
            foreach ((string field, string value) in overrides)
            {
                ApplyScalar(
                    stage,
                    field,
                    value,
                    StageFields,
                    $"clip {clipIndex} stage {stageIndex}",
                    warn);
            }
        }
    }

    private static void ApplyScalar(
        JObject target,
        string field,
        string value,
        Dictionary<string, (string Canonical, OverrideKind Kind)> allowed,
        string location,
        Action<string> warn)
    {
        if (!allowed.TryGetValue(field?.Trim() ?? "", out (string Canonical, OverrideKind Kind) spec))
        {
            DocumentJson.Warn(
                warn,
                $"VideoStages: ignoring unknown or non-overridable {location} field '{field}'.");
            return;
        }
        string trimmed = value?.Trim() ?? "";
        JToken parsedToken;
        switch (spec.Kind)
        {
            case OverrideKind.Int when int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue):
                parsedToken = intValue;
                break;
            case OverrideKind.Double when double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue):
                parsedToken = doubleValue;
                break;
            case OverrideKind.Bool when bool.TryParse(trimmed, out bool boolValue):
                parsedToken = boolValue;
                break;
            case OverrideKind.String:
                parsedToken = trimmed;
                break;
            default:
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: ignoring {location} override '{spec.Canonical}' with invalid value '{value}'.");
                return;
        }
        target[spec.Canonical] = parsedToken;
    }

    private static JObject GetStage(JObject clip, int stageIndex)
    {
        if (DocumentJson.GetToken(clip, "stages") is not JArray array)
        {
            return null;
        }
        List<JObject> stages = [.. array.OfType<JObject>()];
        return stageIndex >= 0 && stageIndex < stages.Count ? stages[stageIndex] : null;
    }

}
