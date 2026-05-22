using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal static class VideoStagesSpecParser
{
    private const double DefaultControl = 0.5;
    private const double FirstStageControl = 1.0;
    private const double DefaultUpscale = 1.0;
    private const string DefaultUpscaleMethod = "pixel-lanczos";
    private const string DefaultGeneratedReference = "Generated";
    private const string DefaultPreviousStageReference = "PreviousStage";
    private const int FrameAlignment = 8;

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

    public static (int? Width, int? Height) GetRawJsonTopLevelDimensions(WorkflowGenerator g)
    {
        (int? w, int? h, _, _) = GetJsonTopLevelConfig(g);
        return (w, h);
    }

    public static AudioFile MaterializeUploadedAudioForClip(WorkflowGenerator g, ClipSpec clip) =>
        MaterializeUploadedAudio(g, clip?.UploadedAudio);

    private static int CalculateAlignedFrameCount(double durationSeconds, int fps)
    {
        int rawFrames = Math.Max(0, (int)Math.Ceiling(durationSeconds * fps));
        int alignedFrames = (int)Math.Ceiling(rawFrames / (double)FrameAlignment) * FrameAlignment;
        return Math.Max(1, alignedFrames + 1);
    }

    private static int? TryGetRegisteredRootDimension(WorkflowGenerator g, T2IRegisteredParam<int> param)
    {
        return g.UserInput.TryGet(param, out int value) && value >= Constants.RootDimensionMin
            ? value
            : null;
    }

    private static int ResolveTopLevelWidth(WorkflowGenerator g, int? rawJsonWidth)
    {
        if (TryGetRegisteredRootDimension(g, VideoStagesExtension.RootWidth) is int registered)
        {
            return registered;
        }
        if (rawJsonWidth is > 0)
        {
            return rawJsonWidth.Value;
        }
        return g.UserInput.GetImageWidth();
    }

    private static int ResolveTopLevelHeight(WorkflowGenerator g, int? rawJsonHeight)
    {
        if (TryGetRegisteredRootDimension(g, VideoStagesExtension.RootHeight) is int registered)
        {
            return registered;
        }
        if (rawJsonHeight is > 0)
        {
            return rawJsonHeight.Value;
        }
        return g.UserInput.GetImageHeight();
    }

    private static int ResolveTopLevelFps(WorkflowGenerator g, int? rawJsonFps)
    {
        if (g.UserInput.TryGet(VideoStagesExtension.RootFPS, out int rootFps) && rootFps > 0)
        {
            return rootFps;
        }
        if (rawJsonFps is > 0)
        {
            return rawJsonFps.Value;
        }
        if (g.UserInput.TryGet(T2IParamTypes.VideoFPS, out int videoFps) && videoFps > 0)
        {
            return videoFps;
        }
        return 24;
    }

    public static bool HasUsableVideoModel(WorkflowGenerator g, VideoStagesSpec spec)
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel)
            && videoModel is not null)
        {
            return true;
        }
        if (g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true)
        {
            return true;
        }
        foreach (ClipSpec clip in spec.Clips)
        {
            foreach (StageSpec stage in clip.Stages)
            {
                if (!string.IsNullOrWhiteSpace(stage.Model) && CanResolveStageVideoModel(g, stage.Model))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool CanResolveStageVideoModel(WorkflowGenerator g, string modelName)
    {
        g.UserInput.Set(T2IParamTypes.VideoModel.Type, modelName);
        bool resolved = g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel m) && m is not null;
        g.UserInput.Remove(T2IParamTypes.VideoModel);
        return resolved;
    }

    public static VideoStagesSpec Parse(WorkflowGenerator g)
    {
        (int? rawWidth, int? rawHeight, int? rawFps, List<JObject> rawEntries) = GetJsonTopLevelConfig(g);
        int width = ResolveTopLevelWidth(g, rawWidth);
        int height = ResolveTopLevelHeight(g, rawHeight);
        int fps = ResolveTopLevelFps(g, rawFps);
        bool isTextToVideo = RootVideoStageHandoff.IsTextToVideoRootWorkflow(g);
        bool refineMode = IsRefineSourceVideoMode(g);
        int refineSkipStages = ResolveRefineSkipStages(g, refineMode);
        if (rawEntries.Count == 0)
        {
            return new VideoStagesSpec(width, height, fps, isTextToVideo, []);
        }

        for (int entryIndex = 0; entryIndex < rawEntries.Count; entryIndex++)
        {
            if (!IsClipShape(rawEntries[entryIndex]))
            {
                throw new SwarmUserErrorException(
                    $"VideoStages: Entry {entryIndex} is not a clip object (must have a 'Stages' array).");
            }
        }

        StageDefaults defaults = BuildDefaults(g);
        List<ClipSpec> parsed = [];
        int globalStageIndex = 0;

        for (int i = 0; i < rawEntries.Count; i++)
        {
            JObject clipObj = rawEntries[i];
            if (GetOptionalBool(clipObj, "Skipped", defaultValue: false))
            {
                continue;
            }
            ClipSpec clip = ParseClip(clipObj, i, defaults, isTextToVideo, fps, refineMode, refineSkipStages);
            if (clip.Stages.Count == 0)
            {
                continue;
            }
            List<StageSpec> activeStages = [];
            int clipStageIndex = 0;
            foreach (StageSpec stage in clip.Stages)
            {
                activeStages.Add(stage with
                {
                    Id = globalStageIndex,
                    ClipStageIndex = clipStageIndex,
                });
                clipStageIndex++;
                globalStageIndex++;
            }
            if (activeStages.Count == 0)
            {
                continue;
            }
            parsed.Add(clip with
            {
                Stages = activeStages,
            });
        }
        return new VideoStagesSpec(width, height, fps, isTextToVideo, parsed);
    }

    private static AudioFile MaterializeUploadedAudio(WorkflowGenerator g, UploadedAudioSpec spec)
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

    private static bool IsClipShape(JObject entry) =>
        entry.Properties().Any(p => StringUtils.Equals(p.Name, "Stages"));

    private static bool IsRefineSourceVideoMode(WorkflowGenerator g)
    {
        return g.UserInput.TryGet(VideoStagesExtension.RefineSourceVideo, out Image source)
            && source is not null;
    }

    private static int ResolveRefineSkipStages(WorkflowGenerator g, bool refineMode)
    {
        if (!refineMode)
        {
            return 0;
        }
        return g.UserInput.TryGet(VideoStagesExtension.RefineSkipStages, out int value) ? value : 1;
    }

    private static ClipSpec ParseClip(
        JObject clipObj,
        int clipIndex,
        StageDefaults defaults,
        bool isTextToVideoRootWorkflow,
        int fps,
        bool refineMode,
        int refineSkipStages)
    {
        double duration = GetOptionalDouble(clipObj, "Duration", defaultValue: 0, $"Clip {clipIndex}");
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
        UploadedAudioSpec uploadedAudio = GetEmbeddedUploadSpec(clipObj, "UploadedAudio");

        List<JObject> rawStages = GetObjectArray(clipObj, "Stages");
        List<StageSpec> stages = [];
        List<JObject> rawRefs = GetObjectArray(clipObj, "Refs");
        bool clipHasWanModel = ClipRawStagesContainWanModel(rawStages, clipIndex);
        int refLimit = clipHasWanModel ? Math.Min(2, rawRefs.Count) : rawRefs.Count;
        List<ImageRefSpec> refs = [];
        for (int i = 0; i < refLimit; i++)
        {
            ImageRefSpec parsedRef = ParseRef(rawRefs[i], clipIndex, i);
            if (parsedRef is not null)
            {
                refs.Add(parsedRef);
            }
        }

        if (clipHasWanModel)
        {
            NormalizeWanClipRefSemantics(refs);
        }

        for (int i = 0; i < rawStages.Count; i++)
        {
            if (GetOptionalBool(rawStages[i], "Skipped", defaultValue: false))
            {
                continue;
            }
            StageSpec parsed = ParseStage(
                rawStages[i],
                clipIndex,
                i,
                defaults,
                refs.Count,
                isTextToVideoRootWorkflow,
                refineMode,
                refineSkipStages);
            stages.Add(parsed);
        }
        ApplyStageContinuationSamplingPlan(stages);

        double durationSeconds = Math.Max(0, duration);
        int? clipFrames = durationSeconds > 0
            ? CalculateAlignedFrameCount(durationSeconds, fps)
            : null;

        return new ClipSpec(
            Id: clipIndex,
            Frames: clipFrames,
            AudioSource: audioSource,
            ControlNetSource: controlNetSource,
            ControlNetLora: controlNetLora,
            SaveAudioTrack: saveAudioTrack,
            ClipLengthFromAudio: clipLengthFromAudio && !clipLengthFromControlNet,
            ClipLengthFromControlNet: clipLengthFromControlNet,
            ReuseAudio: reuseAudio,
            UploadedAudio: uploadedAudio,
            ImageRefs: refs,
            Stages: stages
        );
    }

    private static bool ClipRawStagesContainWanModel(List<JObject> rawStages, int clipIndex)
    {
        for (int i = 0; i < rawStages.Count; i++)
        {
            if (GetOptionalBool(rawStages[i], "Skipped", defaultValue: false))
            {
                continue;
            }

            string model = GetOptionalString(rawStages[i], "Model", defaultValue: null, $"Clip {clipIndex} stage {i}", allowEmpty: false);
            if (VideoStageModelCompat.IsWanVideoModel(model))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyStageContinuationSamplingPlan(List<StageSpec> stages)
    {
        for (int i = 0; i < stages.Count; i++)
        {
            StageSpec stage = stages[i];
            if (!VideoStageModelCompat.IsWanVideoModel(stage.Model))
            {
                continue;
            }

            if (i + 1 < stages.Count)
            {
                StageSpec nextStage = stages[i + 1];
                stages[i] = stage with
                {
                    EndStep = CalculateContinuationEndStep(stage.Steps, nextStage.Control)
                };
            }
        }
    }

    private static int CalculateContinuationEndStep(int steps, double nextStageControl)
    {
        int clampedSteps = Math.Max(1, steps);
        int endStep = (int)Math.Floor(clampedSteps * (1 - Math.Clamp(nextStageControl, 0.0, 1.0)));
        return Math.Clamp(endStep, 0, clampedSteps);
    }

    private static void NormalizeWanClipRefSemantics(List<ImageRefSpec> refs)
    {
        if (refs.Count > 0)
        {
            ImageRefSpec first = refs[0];
            refs[0] = first with
            {
                Frame = 1,
                FromEnd = false
            };
        }

        if (refs.Count > 1)
        {
            ImageRefSpec second = refs[1];
            refs[1] = second with
            {
                Frame = 1,
                FromEnd = true
            };
        }
    }

    private static ImageRefSpec ParseRef(JObject refObj, int clipIndex, int refIndex)
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

        return new ImageRefSpec(
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

    private static (int? Width, int? Height, int? FPS, List<JObject> Entries) GetJsonTopLevelConfig(WorkflowGenerator g)
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
        catch (JsonException ex)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: Could not parse Video Stages JSON. {ex.Message}");
        }
    }

    private static StageDefaults BuildDefaults(WorkflowGenerator g)
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

    private static StageSpec ParseStage(
        JObject stage,
        int clipIndex,
        int index,
        StageDefaults defaults,
        int clipRefCount,
        bool isTextToVideoRootWorkflow,
        bool refineMode,
        int refineSkipStages)
    {
        string locationPrefix = $"Clip {clipIndex} stage {index}";
        string model = GetOptionalString(stage, "Model", defaultValue: null, locationPrefix, allowEmpty: false);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new SwarmUserErrorException(
                $"VideoStages: Clip {clipIndex} stage {index} is missing required field 'Model'.");
        }
        double control = NormalizeControl(GetOptionalDouble(stage, "Control", defaults.Control, locationPrefix));
        double upscale = NormalizeUpscale(GetOptionalDouble(stage, "Upscale", defaults.Upscale, locationPrefix));
        string upscaleMethod = GetOptionalString(
            stage,
            "UpscaleMethod",
            defaults.UpscaleMethod,
            locationPrefix,
            allowEmpty: false);
        bool isRefineSkipped = refineMode && clipIndex == 0 && index < refineSkipStages;
        if (index == 0 || isRefineSkipped)
        {
            if (index == 0 && ShouldWarnFirstStageUpscaleIgnored(
                JsonHasOwnProperty(stage, "Upscale"),
                JsonHasOwnProperty(stage, "UpscaleMethod"),
                upscale))
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
            Id: index,
            Control: control,
            Upscale: upscale,
            UpscaleMethod: upscaleMethod,
            Model: model,
            Vae: NormalizeVaeValue(GetOptionalString(stage, "Vae", defaults.Vae, locationPrefix, allowEmpty: true)),
            Steps: Math.Max(1, GetOptionalInt(stage, "Steps", defaults.Steps, locationPrefix)),
            CfgScale: NormalizeCfgScale(GetOptionalDouble(stage, "CfgScale", defaults.CfgScale, locationPrefix)),
            Sampler: GetOptionalString(stage, "Sampler", defaults.Sampler, locationPrefix, allowEmpty: false),
            Scheduler: GetOptionalString(stage, "Scheduler", defaults.Scheduler, locationPrefix, allowEmpty: false),
            ImageReference: NormalizeImageReference(GetString(stage, "ImageReference"), clipIndex, index, isTextToVideoRootWorkflow),
            ControlNetStrength: ParseStageControlNetStrength(stage, locationPrefix),
            ImageRefStrengths: ParseStageRefStrengths(stage, clipRefCount),
            ImageRefWasExplicit: JsonHasOwnProperty(stage, "ImageReference")
        );
    }

    private static double? ParseStageControlNetStrength(JObject stage, string locationPrefix)
    {
        if (!JsonHasOwnProperty(stage, "ControlNetStrength"))
        {
            return null;
        }

        double value = GetOptionalDouble(stage, "ControlNetStrength", Constants.DefaultStageControlNetStrength, locationPrefix);
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
        return Math.Clamp(value, 0.0, 1.0);
    }

    private static string NormalizeImageReference(string rawValue, int clipIndex, int index, bool isTextToVideoRootWorkflow)
    {
        if (isTextToVideoRootWorkflow)
        {
            if (!string.IsNullOrWhiteSpace(rawValue))
            {
                string rawCompact = StringUtils.Compact(rawValue);
                if (!StringUtils.Equals(rawCompact, DefaultGeneratedReference))
                {
                    Logs.Warning(
                        $"VideoStages: Clip {clipIndex} stage {index} uses ImageReference '{rawValue}' on a text-to-video workflow. "
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
        if (ImageReference.TryParseExplicitStageIndex(compact, out int explicitStage))
        {
            if (explicitStage < index)
            {
                return $"Stage{explicitStage}";
            }
            throw new SwarmUserErrorException(
                $"VideoStages: Clip {clipIndex} stage {index} has invalid ImageReference '{rawValue}' "
                + "(must reference a strictly previous stage).");
        }
        if (ImageReference.TryParseBase2EditStageIndex(compact, out int editStage))
        {
            return ImageReference.FormatBase2EditStageIndex(editStage);
        }

        throw new SwarmUserErrorException(
            $"VideoStages: Clip {clipIndex} stage {index} has invalid ImageReference '{rawValue}'. "
            + "Valid forms are: Generated, Base, Refiner, PreviousStage, Stage<N>, edit<N>.");
    }

    private static string GetDefaultImageReference(int index)
    {
        return index == 0 ? DefaultGeneratedReference : DefaultPreviousStageReference;
    }

    private static string NormalizeVaeValue(string rawVae)
    {
        if (string.IsNullOrWhiteSpace(rawVae))
        {
            return "";
        }
        string trimmed = rawVae.Trim();
        if (StringUtils.Equals(trimmed, "Automatic") || StringUtils.Equals(trimmed, "None"))
        {
            return "";
        }
        return trimmed;
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

    private static string GetOptionalString(
        JObject obj,
        string key,
        string defaultValue,
        string locationPrefix,
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
            Logs.Warning($"VideoStages: {locationPrefix} has empty field '{key}'. Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    private static int GetOptionalInt(JObject obj, string key, int defaultValue, string locationPrefix)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!int.TryParse(raw.Trim(), out int value))
        {
            Logs.Warning(
                $"VideoStages: {locationPrefix} has invalid integer field '{key}' value '{raw}'. "
                + $"Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    private static double GetOptionalDouble(JObject obj, string key, double defaultValue, string locationPrefix)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!double.TryParse(raw.Trim(), out double value))
        {
            Logs.Warning(
                $"VideoStages: {locationPrefix} has invalid numeric field '{key}' value '{raw}'. "
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
