using System.Globalization;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>Applies validated prompt-tag scalar overrides to the authored JSON before parsing.</summary>
internal static class PromptOverrideApplier
{
    private enum OverrideKind { String, Int, Double, Bool }

    private static readonly Dictionary<string, (string Canonical, OverrideKind Kind)> ClipFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["duration"] = ("Duration", OverrideKind.Double),
            ["audiosource"] = ("AudioSource", OverrideKind.String),
            ["saveaudiotrack"] = ("SaveAudioTrack", OverrideKind.Bool),
            ["cliplengthfromaudio"] = ("ClipLengthFromAudio", OverrideKind.Bool),
            ["cliplengthfromcontrolnet"] = ("ClipLengthFromControlNet", OverrideKind.Bool),
            ["reuseaudio"] = ("ReuseAudio", OverrideKind.Bool),
            ["boundaryout"] = ("BoundaryOut", OverrideKind.String),
            ["boundaryoutoverlap"] = ("BoundaryOutOverlap", OverrideKind.Int),
            ["skipped"] = ("Skipped", OverrideKind.Bool),
        };

    private static readonly Dictionary<string, (string Canonical, OverrideKind Kind)> StageFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = ("Model", OverrideKind.String),
            ["steps"] = ("Steps", OverrideKind.Int),
            ["cfgscale"] = ("CfgScale", OverrideKind.Double),
            ["control"] = ("Control", OverrideKind.Double),
            ["upscale"] = ("Upscale", OverrideKind.Double),
            ["upscalemethod"] = ("UpscaleMethod", OverrideKind.String),
            ["sampler"] = ("Sampler", OverrideKind.String),
            ["scheduler"] = ("Scheduler", OverrideKind.String),
            ["imagereference"] = ("ImageReference", OverrideKind.String),
            ["controlnetstrength"] = ("ControlNetStrength", OverrideKind.Double),
            ["skipped"] = ("Skipped", OverrideKind.Bool),
        };

    public static (int? Width, int? Height, int? FPS) ApplyTopLevel(
        PromptParser.VideoStageTagData tags, int? width, int? height, int? fps)
    {
        foreach ((string field, string value) in tags.TopLevelOverrides)
        {
            if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            {
                Logs.Warning($"VideoStages: ignoring invalid top-level override '{field}' = '{value}'.");
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
        PromptParser.VideoStageTagData tags)
    {
        foreach ((int clipIndex, List<(string Field, string Value)> overrides) in tags.ClipOverrides)
        {
            if (clipIndex < 0 || clipIndex >= rawEntries.Count)
            {
                Logs.Warning($"VideoStages: clip override targets out-of-range clip {clipIndex}; ignoring.");
                continue;
            }
            foreach ((string field, string value) in overrides)
            {
                ApplyScalar(rawEntries[clipIndex], field, value, ClipFields, $"clip {clipIndex}");
            }
        }
        foreach (((int clipIndex, int stageIndex), List<(string Field, string Value)> overrides) in tags.StageOverrides)
        {
            if (clipIndex < 0 || clipIndex >= rawEntries.Count)
            {
                Logs.Warning($"VideoStages: stage override targets out-of-range clip {clipIndex}; ignoring.");
                continue;
            }
            JObject stage = GetStage(rawEntries[clipIndex], stageIndex);
            if (stage is null)
            {
                Logs.Warning($"VideoStages: stage override targets out-of-range stage {stageIndex} on clip {clipIndex}; ignoring.");
                continue;
            }
            foreach ((string field, string value) in overrides)
            {
                ApplyScalar(stage, field, value, StageFields, $"clip {clipIndex} stage {stageIndex}");
            }
        }
    }

    private static void ApplyScalar(
        JObject target,
        string field,
        string value,
        Dictionary<string, (string Canonical, OverrideKind Kind)> allowed,
        string location)
    {
        if (!allowed.TryGetValue(field?.Trim() ?? "", out (string Canonical, OverrideKind Kind) spec))
        {
            Logs.Warning($"VideoStages: ignoring unknown or non-overridable {location} field '{field}'.");
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
                Logs.Warning($"VideoStages: ignoring {location} override '{spec.Canonical}' with invalid value '{value}'.");
                return;
        }
        JsonUtil.RemoveAll(target, spec.Canonical);
        target[spec.Canonical] = parsedToken;
    }

    private static JObject GetStage(JObject clip, int stageIndex)
    {
        if (JsonUtil.Get(clip, "Stages") is not JArray array)
        {
            return null;
        }
        List<JObject> stages = [.. array.OfType<JObject>()];
        return stageIndex >= 0 && stageIndex < stages.Count ? stages[stageIndex] : null;
    }
}
