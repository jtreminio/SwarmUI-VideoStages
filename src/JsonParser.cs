using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

public class JsonParser(WorkflowGenerator g)
{
    private const double DefaultControl = 0.5;
    private const double FirstStageControl = 1.0;
    private const double DefaultUpscale = 1.0;
    private const string DefaultUpscaleMethod = "pixel-lanczos";
    private const string DefaultGeneratedReference = "Generated";
    private const string DefaultPreviousStageReference = "PreviousStage";
    private const int FrameAlignment = 8;

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
        int? ClipWidth = null,
        int? ClipHeight = null,
        int? ClipFrames = null,
        int? ClipFPS = null,
        bool ClipReuseAudio = false,
        string ClipControlNetSource = null,
        string ClipControlNetLora = null,
        int ClipStageIndex = 0,
        int ClipStageCount = 0,
        double? ControlNetStrength = null,
        IReadOnlyList<RefSpec> ClipRefs = null,
        IReadOnlyList<double> RefStrengths = null,
        bool ImageReferenceWasExplicit = false
    );

    public sealed record RefSpec(
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
        IReadOnlyList<RefSpec> Refs,
        IReadOnlyList<StageSpec> Stages
    );

    public sealed record VideoStagesSpec(
        int? Width,
        int? Height,
        int? FPS,
        IReadOnlyList<ClipSpec> Clips
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

    public List<StageSpec> ParseStages()
    {
        VideoStagesSpec config = ParseConfig();
        int? registeredRootWidth = ResolveRegisteredRootDimension(VideoStagesExtension.RootWidth);
        int? registeredRootHeight = ResolveRegisteredRootDimension(VideoStagesExtension.RootHeight);
        int? effectiveRootWidth = registeredRootWidth ?? config.Width;
        int? effectiveRootHeight = registeredRootHeight ?? config.Height;
        int fps = ResolveFps(config);
        List<StageSpec> flattened = [];
        int globalStageIndex = 0;
        foreach (ClipSpec clip in config.Clips)
        {
            if (clip.Skipped)
            {
                continue;
            }
            int? clipFrames = null;
            if (clip.DurationSeconds > 0 && fps > 0)
            {
                clipFrames = CalculateAlignedFrameCount(clip.DurationSeconds, fps);
            }
            int activeStageCount = 0;
            for (int s = 0; s < clip.Stages.Count; s++)
            {
                if (!clip.Stages[s].Skipped)
                {
                    activeStageCount++;
                }
            }
            int clipStageIndex = 0;
            for (int s = 0; s < clip.Stages.Count; s++)
            {
                StageSpec stage = clip.Stages[s];
                if (stage.Skipped)
                {
                    continue;
                }
                flattened.Add(stage with
                {
                    Id = globalStageIndex,
                    ClipId = clip.Id,
                    ClipAudioSource = clip.AudioSource,
                    ClipLengthFromAudio = clip.ClipLengthFromAudio && !clip.ClipLengthFromControlNet,
                    ClipLengthFromControlNet = clip.ClipLengthFromControlNet,
                    ClipReuseAudio = clip.ReuseAudio,
                    ClipControlNetSource = clip.ControlNetSource,
                    ClipStageIndex = clipStageIndex,
                    ClipStageCount = activeStageCount,
                    ClipWidth = effectiveRootWidth ?? clip.Width,
                    ClipHeight = effectiveRootHeight ?? clip.Height,
                    ClipFrames = clipFrames,
                    ClipFPS = fps,
                    ClipRefs = clip.Refs,
                    ClipControlNetLora = clip.ControlNetLora,
                });
                clipStageIndex++;
                globalStageIndex++;
            }
        }
        return flattened;
    }

    public int ResolveFps(VideoStagesSpec parsedConfig = null)
    {
        if (g.UserInput.TryGet(VideoStagesExtension.RootFPS, out int rootFps) && rootFps > 0)
        {
            return rootFps;
        }
        int? configFps = parsedConfig is not null ? parsedConfig.FPS : ParseConfig().FPS;
        if (configFps.HasValue && configFps.Value > 0)
        {
            return configFps.Value;
        }
        if (g.UserInput.TryGet(T2IParamTypes.VideoFPS, out int videoFps))
        {
            return videoFps;
        }
        return 24;
    }

    private static int CalculateAlignedFrameCount(double durationSeconds, int fps)
    {
        int rawFrames = Math.Max(0, (int)Math.Ceiling(durationSeconds * fps));
        int alignedFrames = (int)Math.Ceiling(rawFrames / (double)FrameAlignment) * FrameAlignment;
        return Math.Max(1, alignedFrames + 1);
    }

    private int? ResolveRegisteredRootDimension(T2IRegisteredParam<int> param)
    {
        return g.UserInput.TryGet(param, out int value) && value >= Constants.RootDimensionMin
            ? value
            : null;
    }

    public VideoStagesSpec ParseConfig()
    {
        (int? width, int? height, int? fps, List<JObject> rawEntries) = GetJsonTopLevelConfig();
        if (rawEntries.Count == 0)
        {
            return new VideoStagesSpec(width, height, fps, []);
        }

        if (!rawEntries.All(IsClipShape))
        {
            Logs.Warning("VideoStages: Each Video Stages entry must be a clip object with a Stages array.");
            return new VideoStagesSpec(width, height, fps, []);
        }

        StageDefaults defaults = BuildDefaults();
        bool isTextToVideoRootWorkflow = RootVideoStageTakeover.IsTextToVideoRootWorkflow(g);
        List<ClipSpec> parsed = [];

        for (int i = 0; i < rawEntries.Count; i++)
        {
            JObject clipObj = rawEntries[i];
            ClipSpec clip = ParseClip(clipObj, i, defaults, isTextToVideoRootWorkflow);
            if (clip.Stages.Count > 0)
            {
                parsed.Add(clip);
            }
        }
        return new VideoStagesSpec(width, height, fps, parsed);
    }

    public AudioFile ParseUploadedAudioForClip(ClipSpec clip) =>
        MaterializeUploadedAudio(clip?.UploadedAudio);

    private AudioFile MaterializeUploadedAudio(UploadedAudioSpec spec)
    {
        if (spec is null || string.IsNullOrWhiteSpace(spec.Data))
        {
            return null;
        }

        string trimmed = spec.Data.Trim();
        string material = trimmed;
        if (trimmed.StartsWith("inputs/")
            || trimmed.StartsWith("raw/")
            || trimmed.StartsWith("Starred/"))
        {
            if (g.UserInput?.SourceSession is null)
            {
                Logs.Warning(
                    "VideoStages: uploaded audio uses a server-side path (inputs/, raw/, or Starred/) "
                    + "but no session is available; cannot load the file.");
                return null;
            }

            try
            {
                material = T2IParamTypes.FilePathToDataString(
                    g.UserInput.SourceSession,
                    trimmed,
                    "for VideoStages uploaded audio");
            }
            catch (SwarmReadableErrorException ex)
            {
                Logs.Warning(
                    $"VideoStages: Could not resolve uploaded audio path '{trimmed}': {ex.Message}");
                return null;
            }
        }

        try
        {
            AudioFile audio = AudioFile.FromDataString(material);
            audio.SourceFilePath = string.IsNullOrWhiteSpace(spec.FileName)
                ? null
                : spec.FileName.Trim();
            return audio;
        }
        catch (Exception)
        {
            Logs.Warning("VideoStages: Ignoring invalid uploaded audio embedded in Video Stages JSON.");
            return null;
        }
    }

    public List<ClipSpec> ParseClips()
    {
        return [.. ParseConfig().Clips];
    }

    private static bool IsClipShape(JObject entry) =>
        entry.Properties().Any(p => StringUtils.Equals(p.Name, "Stages"));

    private ClipSpec ParseClip(
        JObject clipObj,
        int clipIndex,
        StageDefaults defaults,
        bool isTextToVideoRootWorkflow)
    {
        bool skipped = GetOptionalBool(clipObj, "Skipped", defaultValue: false);
        double duration = GetOptionalDouble(clipObj, "Duration", defaultValue: 0, clipIndex);
        string audioSource = GetString(clipObj, "AudioSource");
        if (string.IsNullOrWhiteSpace(audioSource))
        {
            audioSource = Constants.AudioSourceNative;
        }
        else
        {
            audioSource = audioSource.Trim();
        }
        bool saveAudioTrack = GetOptionalBool(clipObj, "SaveAudioTrack", defaultValue: false);
        bool clipLengthFromAudio = GetOptionalBool(clipObj, "ClipLengthFromAudio", defaultValue: false);
        bool clipLengthFromControlNet = GetOptionalBool(clipObj, "ClipLengthFromControlNet", defaultValue: false);
        bool reuseAudio = GetOptionalBool(clipObj, "ReuseAudio", defaultValue: false);
        string controlNetSource = NormalizeControlNetSource(GetString(clipObj, "ControlNetSource"));
        string controlNetLora = NormalizeControlNetLora(GetString(clipObj, "ControlNetLora"));
        int? width = GetOptionalNullableInt(clipObj, "Width");
        int? height = GetOptionalNullableInt(clipObj, "Height");
        UploadedAudioSpec uploadedAudio = GetEmbeddedUploadSpec(clipObj, "UploadedAudio");

        List<JObject> rawStages = GetObjectArray(clipObj, "Stages");
        List<StageSpec> stages = [];
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

        for (int i = 0; i < rawStages.Count; i++)
        {
            if (!TryParseStage(
                    rawStages[i],
                    i,
                    defaults,
                    refs.Count,
                    isTextToVideoRootWorkflow,
                    out StageSpec parsed))
            {
                continue;
            }
            stages.Add(parsed);
        }

        return new ClipSpec(
            Id: clipIndex,
            Skipped: skipped,
            DurationSeconds: Math.Max(0, duration),
            AudioSource: audioSource,
            ControlNetSource: controlNetSource,
            ControlNetLora: controlNetLora,
            SaveAudioTrack: saveAudioTrack,
            ClipLengthFromAudio: clipLengthFromAudio && !clipLengthFromControlNet,
            ClipLengthFromControlNet: clipLengthFromControlNet,
            ReuseAudio: reuseAudio,
            Width: width,
            Height: height,
            UploadedAudio: uploadedAudio,
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
        if (!string.IsNullOrWhiteSpace(rawFromEnd) && bool.TryParse(rawFromEnd.Trim(), out bool parsedFromEnd))
        {
            fromEnd = parsedFromEnd;
        }
        string uploadFileName = GetString(refObj, "UploadFileName");
        string data = GetString(refObj, "Data");
        UploadedAudioSpec embeddedImage = GetEmbeddedUploadSpec(refObj, "UploadedImage");
        if (embeddedImage is not null)
        {
            data = embeddedImage.Data;
            if (string.IsNullOrWhiteSpace(uploadFileName)
                && !string.IsNullOrWhiteSpace(embeddedImage.FileName))
            {
                uploadFileName = embeddedImage.FileName;
            }
        }

        return new RefSpec(
            Source: source.Trim(),
            Frame: frame,
            FromEnd: fromEnd,
            UploadFileName: string.IsNullOrWhiteSpace(uploadFileName) ? null : uploadFileName.Trim(),
            Data: string.IsNullOrWhiteSpace(data) ? null : data.Trim()
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
            if (StringUtils.Equals(property.Name, key)
                && property.Value is JArray array)
            {
                return [.. array.OfType<JObject>()];
            }
        }
        return [];
    }

    private static JObject GetObject(JObject obj, string key)
    {
        foreach (JProperty property in obj.Properties())
        {
            if (StringUtils.Equals(property.Name, key)
                && property.Value is JObject nested)
            {
                return nested;
            }
        }
        return null;
    }

    private static UploadedAudioSpec GetEmbeddedUploadSpec(JObject parent, string containerPropertyName)
    {
        JObject nested = GetObject(parent, containerPropertyName);
        if (nested is null)
        {
            return null;
        }

        string data = GetString(nested, "Data");
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        return new UploadedAudioSpec(
            Data: data.Trim(),
            FileName: GetString(nested, "FileName")?.Trim()
        );
    }

    private (int? Width, int? Height, int? FPS, List<JObject> Entries) GetJsonTopLevelConfig()
    {
        if (!g.UserInput.TryGet(VideoStagesExtension.VideoStagesJson, out string json)
            || string.IsNullOrWhiteSpace(json))
        {
            return (null, null, null, []);
        }

        try
        {
            JToken token = JToken.Parse(json);
            if (token is JArray array)
            {
                return (null, null, null, [.. array.OfType<JObject>()]);
            }
            if (token is JObject obj)
            {
                return (
                    GetOptionalNullableInt(obj, "Width"),
                    GetOptionalNullableInt(obj, "Height"),
                    GetOptionalNullableInt(obj, "FPS"),
                    GetObjectArray(obj, "Clips")
                );
            }
            return (null, null, null, []);
        }
        catch (Exception)
        {
            Logs.Warning("VideoStages: Ignoring invalid Video Stages JSON.");
            return (null, null, null, []);
        }
    }

    private StageDefaults BuildDefaults()
    {
        int steps = g.UserInput.TryGet(T2IParamTypes.VideoSteps, out int explicitVideoSteps)
            ? explicitVideoSteps
            : g.UserInput.Get(T2IParamTypes.Steps, 8, autoFixDefault: true);
        double cfgScale = g.UserInput.TryGet(T2IParamTypes.VideoCFG, out double explicitVideoCfg)
            ? explicitVideoCfg
            : g.UserInput.Get(T2IParamTypes.CFGScale, 1, autoFixDefault: true);
        string sampler = ComfyUIBackendExtension.SamplerParam is null
            ? "euler"
            : g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler", autoFixDefault: true);
        string scheduler = ComfyUIBackendExtension.SchedulerParam is null
            ? "normal"
            : g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, "normal", autoFixDefault: true);

        return new StageDefaults(
            Control: NormalizeControl(DefaultControl),
            Upscale: NormalizeUpscale(DefaultUpscale),
            UpscaleMethod: DefaultUpscaleMethod,
            Vae: "",
            Steps: Math.Max(1, steps),
            CfgScale: NormalizeCfgScale(cfgScale),
            Sampler: sampler,
            Scheduler: scheduler
        );
    }

    private bool TryParseStage(
        JObject stage,
        int index,
        StageDefaults defaults,
        int clipRefCount,
        bool isTextToVideoRootWorkflow,
        out StageSpec parsedStage)
    {
        parsedStage = null;
        string model = GetOptionalString(stage, "Model", defaultValue: null, index, allowEmpty: false);
        if (string.IsNullOrWhiteSpace(model))
        {
            Logs.Warning($"VideoStages: Stage {index} is missing required field 'Model' and will be skipped.");
            return false;
        }
        double control = NormalizeControl(GetOptionalDouble(stage, "Control", defaults.Control, index));
        double upscale = NormalizeUpscale(GetOptionalDouble(stage, "Upscale", defaults.Upscale, index));
        string upscaleMethod = GetOptionalString(
            stage,
            "UpscaleMethod",
            defaults.UpscaleMethod,
            index,
            allowEmpty: false);
        if (index == 0)
        {
            bool hasUpscaleKey = JsonHasOwnProperty(stage, "Upscale");
            bool hasUpscaleMethodKey = JsonHasOwnProperty(stage, "UpscaleMethod");
            if (ShouldWarnFirstStageUpscaleIgnored(hasUpscaleKey, hasUpscaleMethodKey, upscale))
            {
                Logs.Warning(
                    "VideoStages: The first stage in each clip (stage index 0) includes 'Upscale' / 'UpscaleMethod', "
                    + "which are ignored for that stage only.");
            }
            control = FirstStageControl;
            upscale = DefaultUpscale;
            upscaleMethod = DefaultUpscaleMethod;
        }
        else
        {
            upscale = Math.Max(0.25, upscale);
        }
        string vae = NormalizeVaeValue(GetOptionalString(stage, "Vae", defaults.Vae, index, allowEmpty: true));
        int steps = GetOptionalInt(stage, "Steps", defaults.Steps, index);
        double cfgScale = NormalizeCfgScale(GetOptionalDouble(stage, "CfgScale", defaults.CfgScale, index));
        string sampler = GetOptionalString(stage, "Sampler", defaults.Sampler, index, allowEmpty: false);
        string scheduler = GetOptionalString(stage, "Scheduler", defaults.Scheduler, index, allowEmpty: false);
        bool hasImageReferenceKey = JsonHasOwnProperty(stage, "ImageReference");
        string imageReference = NormalizeImageReference(
            GetString(stage, "ImageReference"),
            index,
            isTextToVideoRootWorkflow);
        bool skipped = GetOptionalBool(stage, "Skipped", defaultValue: false);
        double? controlNetStrength = ParseStageControlNetStrength(stage);
        IReadOnlyList<double> refStrengths = ParseStageRefStrengths(stage, clipRefCount);

        parsedStage = new StageSpec(
            Id: index,
            Control: control,
            Upscale: upscale,
            UpscaleMethod: upscaleMethod,
            Model: model,
            Vae: vae,
            Steps: Math.Max(1, steps),
            CfgScale: cfgScale,
            Sampler: sampler,
            Scheduler: scheduler,
            ImageReference: imageReference,
            Skipped: skipped,
            ControlNetStrength: controlNetStrength,
            ClipRefs: null,
            RefStrengths: refStrengths,
            ImageReferenceWasExplicit: hasImageReferenceKey
        );
        return true;
    }

    private static double? ParseStageControlNetStrength(JObject stage)
    {
        if (!JsonHasOwnProperty(stage, "ControlNetStrength"))
        {
            return null;
        }

        double value = GetOptionalDouble(stage, "ControlNetStrength", Constants.DefaultStageControlNetStrength, 0);
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return Constants.DefaultStageControlNetStrength;
        }
        return Math.Clamp(value, 0.0, 1.0);
    }

    private static IReadOnlyList<double> ParseStageRefStrengths(JObject stage, int clipRefCount)
    {
        if (clipRefCount <= 0)
        {
            return [];
        }

        double defaultStrength = Constants.DefaultStageRefStrength;
        List<double> strengths = [];
        foreach (JProperty property in stage.Properties())
        {
            if (!StringUtils.Equals(property.Name, "refStrengths")
                || property.Value is not JArray array)
            {
                continue;
            }

            foreach (JToken entry in array)
            {
                if (entry.Type == JTokenType.Float || entry.Type == JTokenType.Integer)
                {
                    strengths.Add(ClampRefStrength(entry.Value<double>()));
                }
                else if (entry.Type == JTokenType.String
                    && double.TryParse($"{entry}".Trim(), out double parsed))
                {
                    strengths.Add(ClampRefStrength(parsed));
                }
            }
            break;
        }

        while (strengths.Count < clipRefCount)
        {
            strengths.Add(defaultStrength);
        }

        if (strengths.Count > clipRefCount)
        {
            strengths.RemoveRange(clipRefCount, strengths.Count - clipRefCount);
        }

        return strengths;
    }

    private static double ClampRefStrength(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return Constants.DefaultStageRefStrength;
        }
        return Math.Clamp(value, 0.01, 1.0);
    }

    private string NormalizeImageReference(string rawValue, int index, bool isTextToVideoRootWorkflow)
    {
        if (isTextToVideoRootWorkflow)
        {
            if (!string.IsNullOrWhiteSpace(rawValue))
            {
                string rawCompact = StringUtils.Compact(rawValue);
                if (!StringUtils.Equals(rawCompact, DefaultGeneratedReference))
                {
                    Logs.Warning(
                        $"VideoStages: Stage {index} uses ImageReference '{rawValue}' on a text-to-video workflow. "
                        + $"Using '{DefaultGeneratedReference}' instead.");
                }
            }

            return DefaultGeneratedReference;
        }

        string defaultReference = GetDefaultImageReference(index);
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
        if (StringUtils.Equals(compact, "PreviousStage"))
        {
            return index == 0 ? defaultReference : DefaultPreviousStageReference;
        }
        if (ImageReferenceSyntax.TryParseExplicitStageIndex(compact, out int explicitStage))
        {
            if (explicitStage < index)
            {
                return $"Stage{explicitStage}";
            }
            Logs.Warning(
                $"VideoStages: Stage {index} has forward ImageReference '{rawValue}'. "
                + $"Using '{defaultReference}' instead.");
            return defaultReference;
        }
        if (ImageReferenceSyntax.TryParseBase2EditStageIndex(compact, out int editStage))
        {
            return ImageReferenceSyntax.FormatBase2EditStageIndex(editStage);
        }

        Logs.Warning(
            $"VideoStages: Stage {index} has invalid ImageReference '{rawValue}'. Using '{defaultReference}' instead.");
        return defaultReference;
    }

    private static string GetDefaultImageReference(int index)
    {
        return index == 0 ? DefaultGeneratedReference : DefaultPreviousStageReference;
    }

    private static string NormalizeVaeValue(string rawVae)
    {
        return IsUsableVaeValue(rawVae) ? rawVae.Trim() : "";
    }

    private static string NormalizeOptionalModelName(string rawModelName)
    {
        return string.IsNullOrWhiteSpace(rawModelName) ? "" : rawModelName.Trim();
    }

    private static string NormalizeControlNetLora(string raw)
    {
        string trimmed = NormalizeOptionalModelName(raw);
        if (trimmed.Length == 0)
        {
            return "";
        }
        string squeezed = new([.. trimmed.Where(c => !char.IsWhiteSpace(c))]);
        if (StringUtils.Equals(squeezed, "(none)"))
        {
            return "";
        }
        return trimmed;
    }

    public static bool IsUsableVaeValue(string rawVae)
    {
        if (string.IsNullOrWhiteSpace(rawVae))
        {
            return false;
        }
        string t = rawVae.Trim();
        return !StringUtils.Equals(t, "Automatic") && !StringUtils.Equals(t, "None");
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
            Logs.Warning(
                $"VideoStages: Stage {index} has invalid integer field '{key}' value '{raw}'. "
                + $"Using default '{defaultValue}'.");
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
            Logs.Warning(
                $"VideoStages: Stage {index} has invalid numeric field '{key}' value '{raw}'. "
                + $"Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    private static string GetString(JObject obj, string key)
    {
        foreach (JProperty property in obj.Properties())
        {
            if (StringUtils.Equals(property.Name, key))
            {
                return property.Value?.Type == JTokenType.Null ? null : $"{property.Value}";
            }
        }
        return null;
    }

    private static bool JsonHasOwnProperty(JObject obj, string key) =>
        obj.Properties().Any(p => StringUtils.Equals(p.Name, key));

    private static double TruncateToDecimals(double value, int decimals)
    {
        double factor = Math.Pow(10, decimals);
        return Math.Truncate(value * factor) / factor;
    }

    private static double NormalizeControl(double control) =>
        TruncateToDecimals(Math.Clamp(control, 0, 1), 2);

    private static double NormalizeUpscale(double upscale) =>
        TruncateToDecimals(upscale, 2);

    private static double NormalizeCfgScale(double cfgScale) =>
        TruncateToDecimals(cfgScale, 1);

    private static bool ShouldWarnFirstStageUpscaleIgnored(
        bool hasUpscaleKey,
        bool hasUpscaleMethodKey,
        double upscale)
    {
        if (!hasUpscaleKey && !hasUpscaleMethodKey)
        {
            return false;
        }
        if (NormalizeUpscale(upscale) == 1)
        {
            return false;
        }
        return true;
    }

    private static string NormalizeControlNetSource(string source)
    {
        string compact = StringUtils.Compact(source);
        if (StringUtils.Equals(compact, "ControlNet3"))
        {
            return Constants.ControlNetSourceThree;
        }
        if (StringUtils.Equals(compact, "ControlNet2"))
        {
            return Constants.ControlNetSourceTwo;
        }
        return Constants.ControlNetSourceOne;
    }
}
