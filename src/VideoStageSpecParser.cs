using System.Globalization;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages;

internal sealed record StageParserDefaults(
    double Control,
    double Upscale,
    string UpscaleMethod,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler);

/// <summary>Parses and normalizes one authored generation stage.</summary>
internal static class VideoStageSpecParser
{
    private const double DefaultControl = 0.5;
    private const double FirstStageControl = 1.0;
    private const double DefaultUpscale = 1.0;
    private const string DefaultUpscaleMethod = "pixel-lanczos";
    private const string DefaultGeneratedReference = "Generated";
    private const string DefaultPreviousStageReference = "PreviousStage";

    public static StageParserDefaults BuildDefaults(WorkflowGenerator g)
    {
        (int steps, double cfgScale, string sampler, string scheduler) =
            StageDefaultsProvider.ReadHostDefaults(g);
        return new StageParserDefaults(
            NormalizeControl(DefaultControl),
            NormalizeUpscale(DefaultUpscale),
            DefaultUpscaleMethod,
            steps,
            NormalizeCfgScale(cfgScale),
            sampler,
            scheduler);
    }

    public static StageSpec Parse(
        JObject stage,
        int clipIndex,
        int stageIndex,
        StageParserDefaults defaults,
        int clipRefCount,
        bool isTextToVideoRootWorkflow,
        bool refineMode,
        int refineSkipStages,
        bool sourcedClip)
    {
        string location = $"Clip {clipIndex} stage {stageIndex}";
        string model = VideoStagesJsonReader.GetOptionalString(
            stage, "Model", null, location, allowEmpty: false);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new SwarmUserErrorException(
                $"VideoStages: Clip {clipIndex} stage {stageIndex} is missing required field 'Model'.");
        }

        double control = NormalizeControl(VideoStagesJsonReader.GetOptionalDouble(
            stage, "Control", defaults.Control, location));
        double upscale = NormalizeUpscale(VideoStagesJsonReader.GetOptionalDouble(
            stage, "Upscale", defaults.Upscale, location));
        string upscaleMethod = VideoStagesJsonReader.GetOptionalString(
            stage, "UpscaleMethod", defaults.UpscaleMethod, location, allowEmpty: false);

        bool isRefineSkipped = refineMode && clipIndex == 0 && stageIndex < refineSkipStages;
        bool isGenerationFirstStage = stageIndex == 0 && !sourcedClip;
        if (isGenerationFirstStage || isRefineSkipped)
        {
            if (isGenerationFirstStage && ShouldWarnFirstStageUpscaleIgnored(stage, upscale))
            {
                Logs.Warning(
                    "VideoStages: The first stage in each clip (stage index 0) includes 'Upscale' / 'UpscaleMethod', "
                    + "which are ignored for that stage only.");
            }
            control = isRefineSkipped ? 0.0 : FirstStageControl;
            upscale = DefaultUpscale;
            upscaleMethod = DefaultUpscaleMethod;
        }
        else
        {
            upscale = Math.Max(0.25, upscale);
        }

        return new StageSpec(
            Id: stageIndex,
            Control: control,
            Upscale: upscale,
            UpscaleMethod: upscaleMethod,
            Model: model,
            Steps: Math.Max(1, VideoStagesJsonReader.GetOptionalInt(
                stage, "Steps", defaults.Steps, location)),
            CfgScale: NormalizeCfgScale(VideoStagesJsonReader.GetOptionalDouble(
                stage, "CfgScale", defaults.CfgScale, location)),
            Sampler: VideoStagesJsonReader.GetOptionalString(
                stage, "Sampler", defaults.Sampler, location, allowEmpty: false),
            Scheduler: VideoStagesJsonReader.GetOptionalString(
                stage, "Scheduler", defaults.Scheduler, location, allowEmpty: false),
            ImageReference: NormalizeImageReference(
                VideoStagesJsonReader.GetString(stage, "ImageReference"),
                clipIndex,
                stageIndex,
                isTextToVideoRootWorkflow),
            ControlNetStrength: ParseControlNetStrength(stage, location),
            ImageRefStrengths: ParseRefStrengths(stage, clipRefCount),
            ImageRefWasExplicit: VideoStagesJsonReader.HasProperty(stage, "ImageReference"),
            Loras: VideoStageResourceParser.ParseLoras(stage));
    }

    private static double? ParseControlNetStrength(JObject stage, string location)
    {
        if (!VideoStagesJsonReader.HasProperty(stage, "ControlNetStrength"))
        {
            return null;
        }
        double value = VideoStagesJsonReader.GetOptionalDouble(
            stage,
            "ControlNetStrength",
            Constants.DefaultStageControlNetStrength,
            location);
        return ClampUnitOrDefault(value, Constants.DefaultStageControlNetStrength);
    }

    private static IReadOnlyList<double> ParseRefStrengths(JObject stage, int clipRefCount)
    {
        if (clipRefCount <= 0)
        {
            return [];
        }

        List<double> strengths = [];
        if (JsonUtil.Get(stage, "refStrengths") is JArray array)
        {
            foreach (JToken entry in array)
            {
                if (entry.Type is JTokenType.Float or JTokenType.Integer)
                {
                    strengths.Add(ClampUnitOrDefault(
                        entry.Value<double>(), Constants.DefaultStageRefStrength));
                }
                else if (entry.Type == JTokenType.String
                    && double.TryParse(
                        $"{entry}".Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double parsed))
                {
                    strengths.Add(ClampUnitOrDefault(parsed, Constants.DefaultStageRefStrength));
                }
            }
        }

        while (strengths.Count < clipRefCount)
        {
            strengths.Add(Constants.DefaultStageRefStrength);
        }
        if (strengths.Count > clipRefCount)
        {
            strengths.RemoveRange(clipRefCount, strengths.Count - clipRefCount);
        }
        return strengths;
    }

    private static string NormalizeImageReference(
        string rawValue,
        int clipIndex,
        int stageIndex,
        bool isTextToVideoRootWorkflow)
    {
        if (isTextToVideoRootWorkflow)
        {
            if (!string.IsNullOrWhiteSpace(rawValue)
                && !StringUtils.Equals(StringUtils.Compact(rawValue), DefaultGeneratedReference))
            {
                Logs.Warning(
                    $"VideoStages: Clip {clipIndex} stage {stageIndex} uses ImageReference '{rawValue}' on a text-to-video workflow. "
                    + $"Using '{DefaultGeneratedReference}' instead.");
            }
            return DefaultGeneratedReference;
        }

        string defaultReference = stageIndex == 0
            ? DefaultGeneratedReference
            : DefaultPreviousStageReference;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaultReference;
        }

        string compact = StringUtils.Compact(rawValue);
        if (StringUtils.Equals(compact, DefaultGeneratedReference))
        {
            return DefaultGeneratedReference;
        }
        if (StringUtils.Equals(compact, "Base"))
        {
            return "Base";
        }
        if (StringUtils.Equals(compact, "Refiner"))
        {
            return "Refiner";
        }
        if (StringUtils.Equals(compact, DefaultPreviousStageReference))
        {
            return stageIndex == 0 ? defaultReference : DefaultPreviousStageReference;
        }
        if (ImageReference.TryParseExplicitStageIndex(compact, out int explicitStage))
        {
            if (explicitStage < stageIndex)
            {
                return $"Stage{explicitStage}";
            }
            throw new SwarmUserErrorException(
                $"VideoStages: Clip {clipIndex} stage {stageIndex} has invalid ImageReference '{rawValue}' "
                + "(must reference a strictly previous stage).");
        }
        if (ImageReference.TryParseBase2EditStageIndex(compact, out int editStage))
        {
            return ImageReference.FormatBase2EditStageIndex(editStage);
        }

        throw new SwarmUserErrorException(
            $"VideoStages: Clip {clipIndex} stage {stageIndex} has invalid ImageReference '{rawValue}'. "
            + "Valid forms are: Generated, Base, Refiner, PreviousStage, Stage<N>, edit<N>.");
    }

    private static bool ShouldWarnFirstStageUpscaleIgnored(JObject stage, double upscale)
    {
        if (!VideoStagesJsonReader.HasProperty(stage, "Upscale")
            && !VideoStagesJsonReader.HasProperty(stage, "UpscaleMethod"))
        {
            return false;
        }
        return NormalizeUpscale(upscale) != 1;
    }

    private static double ClampUnitOrDefault(double value, double fallback) =>
        IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : fallback;

    private static double NormalizeControl(double control) =>
        TruncateToDecimals(Math.Clamp(control, 0, 1), 2);

    private static double NormalizeUpscale(double upscale) =>
        Math.Round(upscale * 4, MidpointRounding.AwayFromZero) / 4;

    private static double NormalizeCfgScale(double cfgScale) =>
        TruncateToDecimals(cfgScale, 1);

    private static double TruncateToDecimals(double value, int decimals)
    {
        double factor = Math.Pow(10, decimals);
        return Math.Truncate(value * factor) / factor;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
