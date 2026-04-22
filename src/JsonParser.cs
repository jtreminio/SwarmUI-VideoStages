using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

public class JsonParser(WorkflowGenerator g)
{
    private const double DefaultControl = 1.0;
    private const double DefaultUpscale = 1.0;
    private const string DefaultUpscaleMethod = "pixel-lanczos";
    private const string DefaultGeneratedReference = "Generated";
    private const string DefaultPreviousStageReference = "PreviousStage";

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
        int? ClipWidth = null,
        int? ClipHeight = null,
        int? ClipFrames = null
    );

    public sealed record RefSpec(
        string Source,
        int Frame,
        bool FromEnd,
        string UploadFileName
    );

    public sealed record ClipSpec(
        int Id,
        string Name,
        bool Skipped,
        double DurationSeconds,
        int? Width,
        int? Height,
        IReadOnlyList<RefSpec> Refs,
        IReadOnlyList<StageSpec> Stages
    );

    private sealed record StageDefaults(
        double Control,
        double Upscale,
        string UpscaleMethod,
        string Vae,
        int Steps,
        double CfgScale,
        string Sampler,
        string Scheduler
    );

    /// <summary>
    /// Parses the configured JSON. If the JSON is in clip-shape (each entry
    /// has a "Stages" array), returns a flattened list of every clip's stages
    /// in render order; legacy stage-shape JSON is treated as a single clip's
    /// stages. Each stage carries its parent clip's optional width/height/frames
    /// so the runner can apply per-clip overrides. Use <see cref="ParseClips"/>
    /// for clip-aware behavior.
    /// </summary>
    public List<StageSpec> ParseStages()
    {
        List<ClipSpec> clips = ParseClips();
        int fps = ResolveFps();
        List<StageSpec> flattened = [];
        int globalStageIndex = 0;
        foreach (ClipSpec clip in clips)
        {
            if (clip.Skipped)
            {
                continue;
            }
            int? clipFrames = null;
            if (clip.DurationSeconds > 0 && fps > 0)
            {
                clipFrames = Math.Max(1, (int)Math.Round(clip.DurationSeconds * fps));
            }
            foreach (StageSpec stage in clip.Stages)
            {
                if (stage.Skipped)
                {
                    continue;
                }
                flattened.Add(stage with
                {
                    Id = globalStageIndex,
                    ClipWidth = clip.Width,
                    ClipHeight = clip.Height,
                    ClipFrames = clipFrames,
                });
                globalStageIndex++;
            }
        }
        return flattened;
    }

    private int ResolveFps()
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoFPS, out int fps))
        {
            return fps;
        }
        return 24;
    }

    /// <summary>
    /// Parses the configured JSON into clip-shaped data. Legacy stage-only
    /// JSON is wrapped in a single default clip so the rest of the pipeline
    /// can always reason in terms of clips.
    /// </summary>
    public List<ClipSpec> ParseClips()
    {
        List<JObject> rawEntries = GetJsonTopLevelArray();
        if (rawEntries.Count == 0)
        {
            return [];
        }

        bool isClipShaped = rawEntries.Any(IsClipShape);
        StageDefaults defaults = BuildDefaults();
        List<ClipSpec> parsed = [];

        if (!isClipShaped)
        {
            ClipSpec legacyClip = ParseLegacyStageArrayAsSingleClip(rawEntries, defaults);
            if (legacyClip.Stages.Count > 0)
            {
                parsed.Add(legacyClip);
            }
            return parsed;
        }

        for (int i = 0; i < rawEntries.Count; i++)
        {
            JObject clipObj = rawEntries[i];
            ClipSpec clip = ParseClip(clipObj, i, defaults);
            if (clip.Stages.Count > 0)
            {
                parsed.Add(clip);
            }
        }
        return parsed;
    }

    private static bool IsClipShape(JObject entry)
    {
        foreach (JProperty property in entry.Properties())
        {
            if (string.Equals(property.Name, "Stages", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private ClipSpec ParseLegacyStageArrayAsSingleClip(List<JObject> rawStages, StageDefaults defaults)
    {
        List<StageSpec> stages = [];
        for (int i = 0; i < rawStages.Count; i++)
        {
            if (TryParseStage(rawStages[i], i, defaults, out StageSpec normalized))
            {
                stages.Add(normalized);
            }
        }
        return new ClipSpec(
            Id: 0,
            Name: "Clip 0",
            Skipped: false,
            DurationSeconds: 0,
            Width: null,
            Height: null,
            Refs: [],
            Stages: stages
        );
    }

    private ClipSpec ParseClip(JObject clipObj, int clipIndex, StageDefaults defaults)
    {
        bool skipped = GetOptionalBool(clipObj, "Skipped", defaultValue: false);
        string name = GetOptionalString(clipObj, "Name", defaultValue: $"Clip {clipIndex}", clipIndex, allowEmpty: true);
        double duration = GetOptionalDouble(clipObj, "Duration", defaultValue: 0, clipIndex);
        int? width = GetOptionalNullableInt(clipObj, "Width");
        int? height = GetOptionalNullableInt(clipObj, "Height");

        List<JObject> rawStages = GetObjectArray(clipObj, "Stages");
        List<StageSpec> stages = [];
        for (int i = 0; i < rawStages.Count; i++)
        {
            if (!TryParseStage(rawStages[i], i, defaults, out StageSpec parsed))
            {
                continue;
            }
            stages.Add(parsed);
        }

        List<JObject> rawRefs = GetObjectArray(clipObj, "Refs");
        List<RefSpec> refs = [];
        for (int i = 0; i < rawRefs.Count; i++)
        {
            RefSpec parsedRef = ParseRef(rawRefs[i], clipIndex, i);
            if (parsedRef is not null)
            {
                refs.Add(parsedRef);
            }
        }
        if (refs.Count > 0)
        {
            Logs.Warning($"VideoStages: Clip {clipIndex} ({name}) declares {refs.Count} reference image(s); these are recorded in metadata but are not yet wired into ComfyUI keyframe nodes by this extension.");
        }

        return new ClipSpec(
            Id: clipIndex,
            Name: string.IsNullOrWhiteSpace(name) ? $"Clip {clipIndex}" : name,
            Skipped: skipped,
            DurationSeconds: Math.Max(0, duration),
            Width: width,
            Height: height,
            Refs: refs,
            Stages: stages
        );
    }

    private static RefSpec ParseRef(JObject refObj, int clipIndex, int refIndex)
    {
        string source = GetString(refObj, "Source");
        if (string.IsNullOrWhiteSpace(source))
        {
            Logs.Warning($"VideoStages: Clip {clipIndex} reference {refIndex} is missing a Source value; skipping.");
            return null;
        }

        int frame = 1;
        string rawFrame = GetString(refObj, "Frame");
        if (!string.IsNullOrWhiteSpace(rawFrame) && int.TryParse(rawFrame.Trim(), out int parsedFrame))
        {
            frame = Math.Max(1, parsedFrame);
        }
        bool fromEnd = false;
        string rawFromEnd = GetString(refObj, "FromEnd");
        if (!string.IsNullOrWhiteSpace(rawFromEnd))
        {
            _ = bool.TryParse(rawFromEnd.Trim(), out fromEnd);
        }
        string uploadFileName = GetString(refObj, "UploadFileName");
        return new RefSpec(
            Source: source.Trim(),
            Frame: frame,
            FromEnd: fromEnd,
            UploadFileName: string.IsNullOrWhiteSpace(uploadFileName) ? null : uploadFileName.Trim()
        );
    }

    private static bool GetOptionalBool(JObject obj, string key, bool defaultValue)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        return bool.TryParse(raw.Trim(), out bool value) ? value : defaultValue;
    }

    private static int? GetOptionalNullableInt(JObject obj, string key)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return int.TryParse(raw.Trim(), out int value) ? value : null;
    }

    private static List<JObject> GetObjectArray(JObject obj, string key)
    {
        foreach (JProperty property in obj.Properties())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase) && property.Value is JArray array)
            {
                return [.. array.OfType<JObject>()];
            }
        }
        return [];
    }

    private List<JObject> GetJsonTopLevelArray()
    {
        if (!g.UserInput.TryGet(VideoStagesExtension.VideoStagesJson, out string json)
            || string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            JToken token = JToken.Parse(json);
            if (token is not JArray array)
            {
                return [];
            }
            return [.. array.OfType<JObject>()];
        }
        catch
        {
            Logs.Warning("VideoStages: Ignoring invalid Video Stages JSON.");
            return [];
        }
    }

    private StageDefaults BuildDefaults()
    {
        int steps = g.UserInput.TryGet(T2IParamTypes.VideoSteps, out int explicitVideoSteps)
            ? explicitVideoSteps
            : g.UserInput.Get(T2IParamTypes.Steps, 20, autoFixDefault: true);
        double cfgScale = g.UserInput.TryGet(T2IParamTypes.VideoCFG, out double explicitVideoCfg)
            ? explicitVideoCfg
            : g.UserInput.Get(T2IParamTypes.CFGScale, 7, autoFixDefault: true);
        string sampler = ComfyUIBackendExtension.SamplerParam is null
            ? "euler"
            : g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler", autoFixDefault: true);
        string scheduler = ComfyUIBackendExtension.SchedulerParam is null
            ? "normal"
            : g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, "normal", autoFixDefault: true);

        return new StageDefaults(
            Control: DefaultControl,
            Upscale: DefaultUpscale,
            UpscaleMethod: DefaultUpscaleMethod,
            Vae: "",
            Steps: Math.Max(1, steps),
            CfgScale: cfgScale,
            Sampler: sampler,
            Scheduler: scheduler
        );
    }

    private bool TryParseStage(JObject stage, int index, StageDefaults defaults, out StageSpec parsedStage)
    {
        parsedStage = null;
        string model = GetOptionalString(stage, "Model", defaultValue: null, index, allowEmpty: false);
        if (string.IsNullOrWhiteSpace(model))
        {
            Logs.Warning($"VideoStages: Stage {index} is missing required field 'Model' and will be skipped.");
            return false;
        }
        double control = GetOptionalDouble(stage, "Control", defaults.Control, index);
        double upscale = GetOptionalDouble(stage, "Upscale", defaults.Upscale, index);
        string upscaleMethod = GetOptionalString(stage, "UpscaleMethod", defaults.UpscaleMethod, index, allowEmpty: false);
        string vae = NormalizeVaeValue(GetOptionalString(stage, "Vae", defaults.Vae, index, allowEmpty: true));
        int steps = GetOptionalInt(stage, "Steps", defaults.Steps, index);
        double cfgScale = GetOptionalDouble(stage, "CfgScale", defaults.CfgScale, index);
        string sampler = GetOptionalString(stage, "Sampler", defaults.Sampler, index, allowEmpty: false);
        string scheduler = GetOptionalString(stage, "Scheduler", defaults.Scheduler, index, allowEmpty: false);
        string imageReference = NormalizeImageReference(GetString(stage, "ImageReference"), index);
        bool skipped = GetOptionalBool(stage, "Skipped", defaultValue: false);

        parsedStage = new StageSpec(
            Id: index,
            Control: Clamp(control, 0, 1),
            Upscale: Math.Max(0.25, upscale),
            UpscaleMethod: upscaleMethod,
            Model: model,
            Vae: vae,
            Steps: Math.Max(1, steps),
            CfgScale: cfgScale,
            Sampler: sampler,
            Scheduler: scheduler,
            ImageReference: imageReference,
            Skipped: skipped
        );
        return true;
    }

    private string NormalizeImageReference(string rawValue, int index)
    {
        if (IsTextToVideoRootWorkflow())
        {
            if (!string.IsNullOrWhiteSpace(rawValue))
            {
                string rawCompact = rawValue.Trim().Replace(" ", "");
                if (!string.Equals(rawCompact, DefaultGeneratedReference, StringComparison.OrdinalIgnoreCase))
                {
                    Logs.Warning($"VideoStages: Stage {index} uses ImageReference '{rawValue}' on a text-to-video workflow. Using '{DefaultGeneratedReference}' instead.");
                }
            }

            return DefaultGeneratedReference;
        }

        string defaultReference = GetDefaultImageReference(index);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaultReference;
        }

        string compact = ImageReferenceSyntax.Compact(rawValue);
        if (string.Equals(compact, "Generated", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultGeneratedReference;
        }
        if (string.Equals(compact, "Base", StringComparison.OrdinalIgnoreCase))
        {
            return "Base";
        }
        if (string.Equals(compact, "Refiner", StringComparison.OrdinalIgnoreCase))
        {
            return "Refiner";
        }
        if (string.Equals(compact, "PreviousStage", StringComparison.OrdinalIgnoreCase))
        {
            return index == 0 ? defaultReference : DefaultPreviousStageReference;
        }
        if (ImageReferenceSyntax.TryParseExplicitStageIndex(compact, out int explicitStage))
        {
            if (explicitStage < index)
            {
                return $"Stage{explicitStage}";
            }

            Logs.Warning($"VideoStages: Stage {index} has forward ImageReference '{rawValue}'. Using '{defaultReference}' instead.");
            return defaultReference;
        }
        if (ImageReferenceSyntax.TryParseBase2EditStageIndex(compact, out int editStage))
        {
            return ImageReferenceSyntax.FormatBase2EditStageIndex(editStage);
        }

        Logs.Warning($"VideoStages: Stage {index} has invalid ImageReference '{rawValue}'. Using '{defaultReference}' instead.");
        return defaultReference;
    }

    private bool IsTextToVideoRootWorkflow()
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel imageToVideoModel) && imageToVideoModel is not null)
        {
            return false;
        }

        return g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true;
    }

    private static string GetDefaultImageReference(int index)
    {
        return index == 0 ? DefaultGeneratedReference : DefaultPreviousStageReference;
    }

    private static string NormalizeVaeValue(string rawVae)
    {
        return IsUsableVaeValue(rawVae) ? rawVae.Trim() : "";
    }

    public static bool IsUsableVaeValue(string rawVae)
    {
        if (string.IsNullOrWhiteSpace(rawVae))
        {
            return false;
        }
        return !string.Equals(rawVae.Trim(), "Automatic", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rawVae.Trim(), "None", StringComparison.OrdinalIgnoreCase);
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Min(Math.Max(value, min), max);
    }

    private static string GetOptionalString(
        JObject obj,
        string key,
        string defaultValue,
        int index,
        bool allowEmpty)
    {
        string value = GetString(obj, key);
        if (value is null)
        {
            return defaultValue;
        }
        value = value.Trim();
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            Logs.Warning($"VideoStages: Stage {index} has empty field '{key}'. Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    private static int GetOptionalInt(JObject obj, string key, int defaultValue, int index)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!int.TryParse(raw.Trim(), out int value))
        {
            Logs.Warning($"VideoStages: Stage {index} has invalid integer field '{key}' value '{raw}'. Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    private static double GetOptionalDouble(JObject obj, string key, double defaultValue, int index)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!double.TryParse(raw.Trim(), out double value))
        {
            Logs.Warning($"VideoStages: Stage {index} has invalid numeric field '{key}' value '{raw}'. Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    private static string GetString(JObject obj, string key)
    {
        foreach (JProperty property in obj.Properties())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value?.Type == JTokenType.Null ? null : $"{property.Value}";
            }
        }
        return null;
    }
}
