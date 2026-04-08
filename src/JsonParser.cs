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
        string ImageReference
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

    public bool HasConfiguredStages() => ParseStages().Count > 0;

    public List<StageSpec> ParseStages()
    {
        List<JObject> stages = GetJsonStagesArray();
        if (stages.Count == 0)
        {
            return [];
        }

        StageDefaults defaults = BuildDefaults();
        List<StageSpec> parsed = [];
        for (int i = 0; i < stages.Count; i++)
        {
            JObject stage = stages[i];
            if (!TryParseStage(stage, i, defaults, out StageSpec normalized))
            {
                continue;
            }

            parsed.Add(normalized);
        }

        return parsed;
    }

    private List<JObject> GetJsonStagesArray()
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
            ImageReference: imageReference
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
