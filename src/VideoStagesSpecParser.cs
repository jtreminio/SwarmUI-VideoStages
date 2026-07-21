using System.Globalization;
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
        int Steps,
        double CfgScale,
        string Sampler,
        string Scheduler
    );

    public static (int? Width, int? Height) GetRawJsonTopLevelDimensions(WorkflowGenerator g)
    {
        (int? w, int? h, _, _) = GetJsonTopLevelConfig(g);
        if (VideoStagesPromptSection.IsActive(g))
        {
            (w, h, _) = ApplyTopLevelOverrides(ParseTags(g), w, h, null);
        }
        return (w, h);
    }

    public static AudioFile MaterializeUploadedAudioForClip(WorkflowGenerator g, ClipSpec clip) =>
        MaterializeUploadedAudio(g, clip?.UploadedAudio);

    internal static int CalculateAlignedFrameCount(double durationSeconds, int fps)
    {
        int rawFrames = Math.Max(0, (int)Math.Ceiling(durationSeconds * fps));
        int alignedFrames = (int)Math.Ceiling(rawFrames / (double)FrameAlignment) * FrameAlignment;
        return Math.Max(1, alignedFrames + 1);
    }

    private static int ResolveTopLevelWidth(WorkflowGenerator g, int? rawJsonWidth)
    {
        if (rawJsonWidth is > 0)
        {
            return rawJsonWidth.Value;
        }
        return g.UserInput.GetImageWidth();
    }

    private static int ResolveTopLevelHeight(WorkflowGenerator g, int? rawJsonHeight)
    {
        if (rawJsonHeight is > 0)
        {
            return rawJsonHeight.Value;
        }
        return g.UserInput.GetImageHeight();
    }

    private static int ResolveTopLevelFps(WorkflowGenerator g, int? rawJsonFps)
    {
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

    public static VideoStagesSpec Parse(WorkflowGenerator g)
    {
        (int? rawWidth, int? rawHeight, int? rawFps, List<JObject> rawEntries) = GetJsonTopLevelConfig(g);
        PromptParser.VideoStageTagData tags = VideoStagesPromptSection.IsActive(g)
            ? ParseTags(g)
            : new PromptParser.VideoStageTagData();
        (rawWidth, rawHeight, rawFps) = ApplyTopLevelOverrides(tags, rawWidth, rawHeight, rawFps);
        ApplyClipAndStageOverrides(rawEntries, tags);
        int width = ResolveTopLevelWidth(g, rawWidth);
        int height = ResolveTopLevelHeight(g, rawHeight);
        int fps = ResolveTopLevelFps(g, rawFps);
        bool isTextToVideo = RootVideoStageHandoff.IsTextToVideoRootWorkflow(g);
        bool refineMode = VideoStagesGate.IsRefineSourceVideoMode(g);
        int refineSkipStages = VideoStagesGate.ResolveRefineSkipStages(g, refineMode);
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
            ClipSpec clip = ParseClip(clipObj, i, defaults, isTextToVideo, fps, refineMode, refineSkipStages, tags);
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
                    // The position in the clip's authored stage list (skipped stages included) —
                    // the index space the editor uses, e.g. for IcLoraSpec.Stage targeting.
                    ClipStageRawIndex = stage.Id,
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

    private static PromptParser.VideoStageTagData ParseTags(WorkflowGenerator g) =>
        PromptParser.ExtractTagData(g.UserInput.Get(T2IParamTypes.Prompt, ""), g.UserInput);

    private enum OverrideKind { String, Int, Double, Bool }

    private static readonly Dictionary<string, (string Canonical, OverrideKind Kind)> ClipOverrideFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["duration"] = ("Duration", OverrideKind.Double),
            ["audiosource"] = ("AudioSource", OverrideKind.String),
            ["controlnetsource"] = ("ControlNetSource", OverrideKind.String),
            ["controlnetlora"] = ("ControlNetLora", OverrideKind.String),
            ["saveaudiotrack"] = ("SaveAudioTrack", OverrideKind.Bool),
            ["cliplengthfromaudio"] = ("ClipLengthFromAudio", OverrideKind.Bool),
            ["cliplengthfromcontrolnet"] = ("ClipLengthFromControlNet", OverrideKind.Bool),
            ["reuseaudio"] = ("ReuseAudio", OverrideKind.Bool),
            ["boundaryout"] = ("BoundaryOut", OverrideKind.String),
            ["skipped"] = ("Skipped", OverrideKind.Bool),
        };

    private static readonly Dictionary<string, (string Canonical, OverrideKind Kind)> StageOverrideFields =
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

    private static (int? Width, int? Height, int? FPS) ApplyTopLevelOverrides(
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

    private static void ApplyClipAndStageOverrides(List<JObject> rawEntries, PromptParser.VideoStageTagData tags)
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
                ApplyScalarOverride(rawEntries[clipIndex], field, value, ClipOverrideFields, $"clip {clipIndex}");
            }
        }
        foreach (((int clipIndex, int stageIndex), List<(string Field, string Value)> overrides) in tags.StageOverrides)
        {
            if (clipIndex < 0 || clipIndex >= rawEntries.Count)
            {
                Logs.Warning($"VideoStages: stage override targets out-of-range clip {clipIndex}; ignoring.");
                continue;
            }
            JObject stageObj = GetStageObject(rawEntries[clipIndex], stageIndex);
            if (stageObj is null)
            {
                Logs.Warning($"VideoStages: stage override targets out-of-range stage {stageIndex} on clip {clipIndex}; ignoring.");
                continue;
            }
            foreach ((string field, string value) in overrides)
            {
                ApplyScalarOverride(stageObj, field, value, StageOverrideFields, $"clip {clipIndex} stage {stageIndex}");
            }
        }
    }

    private static void ApplyScalarOverride(
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
            case OverrideKind.Int:
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
                {
                    Logs.Warning($"VideoStages: ignoring {location} override '{spec.Canonical}' with invalid value '{value}'.");
                    return;
                }
                parsedToken = intValue;
                break;
            case OverrideKind.Double:
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
                {
                    Logs.Warning($"VideoStages: ignoring {location} override '{spec.Canonical}' with invalid value '{value}'.");
                    return;
                }
                parsedToken = doubleValue;
                break;
            case OverrideKind.Bool:
                if (!bool.TryParse(trimmed, out bool boolValue))
                {
                    Logs.Warning($"VideoStages: ignoring {location} override '{spec.Canonical}' with invalid value '{value}'.");
                    return;
                }
                parsedToken = boolValue;
                break;
            default:
                parsedToken = trimmed;
                break;
        }
        JsonUtil.RemoveAll(target, spec.Canonical);
        target[spec.Canonical] = parsedToken;
    }

    private static JObject GetStageObject(JObject clipObj, int stageIndex)
    {
        if (JsonUtil.Get(clipObj, "Stages") is not JArray array)
        {
            return null;
        }
        List<JObject> stages = [.. array.OfType<JObject>()];
        return stageIndex >= 0 && stageIndex < stages.Count ? stages[stageIndex] : null;
    }

    internal static AudioFile MaterializeUploadedAudio(WorkflowGenerator g, UploadedAudioSpec spec)
    {
        if (spec is null || string.IsNullOrWhiteSpace(spec.Data))
        {
            return null;
        }

        string material = UploadedMediaResolver.ResolveDataString(
            g, spec.Data.Trim(), "uploaded audio", StringComparison.Ordinal);
        if (material is null)
        {
            return null;
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
        JsonUtil.Get(entry, "Stages") is not null;

    /// <summary>
    /// Reads the optional per-clip <c>Retake</c> object (seconds-based, matching every other timeline
    /// field) and converts it to a frame-based <see cref="RetakeWindowSpec"/> at the timeline fps.
    /// Returns <c>null</c> when absent, malformed, or resolving to a non-positive length — that is the
    /// enable gate (a zero-length retake regenerates nothing).
    /// </summary>
    private static RetakeWindowSpec ResolveClipRetakeWindow(JObject clipObj, int fps, int clipIndex)
    {
        JObject retakeObj = GetObject(clipObj, "Retake");
        if (retakeObj is null || fps <= 0)
        {
            return null;
        }
        string location = $"Clip {clipIndex} Retake";
        double startSeconds = GetOptionalDouble(retakeObj, "StartSeconds", 0, location);
        double lengthSeconds = GetOptionalDouble(retakeObj, "LengthSeconds", 0, location);
        if (!IsFinite(startSeconds) || !IsFinite(lengthSeconds)
            || startSeconds < 0 || lengthSeconds <= 0)
        {
            return null;
        }
        int startFrame = (int)Math.Round(startSeconds * fps);
        int lengthFrames = (int)Math.Round(lengthSeconds * fps);
        if (startFrame < 0 || lengthFrames <= 0)
        {
            return null;
        }
        double strength = GetOptionalDouble(retakeObj, "Strength", 1.0, location);
        strength = IsFinite(strength) ? Math.Clamp(strength, 0.0, 1.0) : 1.0;
        return new RetakeWindowSpec(startFrame, lengthFrames, strength);
    }

    private const double AudioSegmentMinLength = 0.1;

    /// <summary>
    /// Reads the optional per-clip <c>AudioSegments</c> array (seconds-based overlay pieces). Each entry
    /// needs a <c>Source</c> — an embedded upload object, or an AceStepFun track ref string ("audio0", …)
    /// — and a positive length; entries are clamped inside the clip duration and sorted by start.
    /// Invalid entries are dropped. Returns an empty list when absent.
    /// </summary>
    private static IReadOnlyList<AudioSegmentSpec> ParseAudioSegments(JObject clipObj, double clipDurationSeconds)
    {
        List<JObject> raw = GetObjectArray(clipObj, UploadContainers.SegmentsCollection);
        if (raw.Count == 0)
        {
            return [];
        }
        List<AudioSegmentSpec> segments = [];
        foreach (JObject segObj in raw)
        {
            UploadedAudioSpec source = GetEmbeddedUploadSpec(segObj, UploadContainers.SegmentSource);
            string aceSource = null;
            if (source is null)
            {
                string sourceRef = GetString(segObj, "Source")?.Trim();
                if (!string.IsNullOrEmpty(sourceRef)
                    && AudioHandler.TryParseAceStepFunAudioSource(sourceRef, out _))
                {
                    aceSource = sourceRef;
                }
            }
            if (source is null && aceSource is null)
            {
                continue;
            }
            double start = GetOptionalDouble(segObj, "StartSeconds", 0, "Clip AudioSegment");
            double trim = GetOptionalDouble(segObj, "TrimStartSeconds", 0, "Clip AudioSegment");
            double length = GetOptionalDouble(segObj, "LengthSeconds", 0, "Clip AudioSegment");
            if (!IsFinite(start) || !IsFinite(trim) || !IsFinite(length) || length <= 0)
            {
                continue;
            }
            start = Math.Max(0, start);
            trim = Math.Max(0, trim);
            if (clipDurationSeconds > 0)
            {
                double maxStart = Math.Max(0, clipDurationSeconds - AudioSegmentMinLength);
                start = Math.Min(start, maxStart);
                length = Math.Clamp(length, AudioSegmentMinLength, Math.Max(AudioSegmentMinLength, clipDurationSeconds - start));
            }
            segments.Add(new AudioSegmentSpec(
                Source: source,
                StartSeconds: RoundTenth(start),
                TrimStartSeconds: RoundTenth(trim),
                LengthSeconds: RoundTenth(length),
                AceStepFunSource: aceSource));
        }
        segments.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        return segments;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static double RoundTenth(double value) => Math.Round(value * 10) / 10;

    private static ClipSpec ParseClip(
        JObject clipObj,
        int clipIndex,
        StageDefaults defaults,
        bool isTextToVideoRootWorkflow,
        int fps,
        bool refineMode,
        int refineSkipStages,
        PromptParser.VideoStageTagData tags)
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
        IReadOnlyList<IcLoraSpec> icLoras = ParseIcLoras(clipObj);
        string boundaryOut = NormalizeBoundaryOut(GetString(clipObj, "BoundaryOut"));
        UploadedAudioSpec uploadedAudio = GetEmbeddedUploadSpec(clipObj, UploadContainers.ClipAudio);
        IReadOnlyList<AudioSegmentSpec> audioSegments = ParseAudioSegments(clipObj, Math.Max(0, duration));

        List<JObject> rawStages = GetObjectArray(clipObj, "Stages");
        // A stage target beyond the authored stage list would silently skip the whole
        // IC-LoRA (loader and guide) on every stage that runs — fail loudly instead.
        for (int i = 0; i < icLoras.Count; i++)
        {
            if (icLoras[i].Stage >= rawStages.Count)
            {
                throw new SwarmUserErrorException(
                    $"VideoStages: Clip {clipIndex} IC-LoRA {i} is set to apply on stage "
                    + $"{icLoras[i].Stage}, but the clip only has {rawStages.Count} stage(s). "
                    + "Set its 'Apply on' to an existing stage or to All stages.");
            }
        }
        List<StageSpec> stages = [];
        List<JObject> rawRefs = GetObjectArray(clipObj, UploadContainers.RefsCollection);
        List<ImageRefSpec> refs = [];
        for (int i = 0; i < rawRefs.Count; i++)
        {
            ImageRefSpec parsedRef = ParseRef(rawRefs[i], clipIndex, i);
            if (parsedRef is not null)
            {
                refs.Add(parsedRef);
            }
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

        // Retake only applies when refining a base video (it reuses the refine-source base). Carry the
        // window on the LAST active stage — the one that re-renders — so earlier skip-range passthrough
        // stages stay untouched. Only the LTX local path reads it; other models ignore it.
        RetakeWindowSpec retakeWindow = refineMode
            ? ResolveClipRetakeWindow(clipObj, fps, clipIndex)
            : null;
        if (retakeWindow is not null && stages.Count > 0)
        {
            stages[^1] = stages[^1] with { RetakeWindow = retakeWindow };
        }

        double durationSeconds = Math.Max(0, duration);
        int? clipFrames = durationSeconds > 0
            ? CalculateAlignedFrameCount(durationSeconds, fps)
            : null;

        return new ClipSpec(
            Id: clipIndex,
            Frames: clipFrames,
            AudioSource: audioSource,
            IcLoras: icLoras,
            SaveAudioTrack: saveAudioTrack,
            ClipLengthFromAudio: clipLengthFromAudio && !clipLengthFromControlNet,
            ClipLengthFromControlNet: clipLengthFromControlNet,
            ReuseAudio: reuseAudio,
            UploadedAudio: uploadedAudio,
            ImageRefs: refs,
            Stages: stages,
            AudioSegments: audioSegments,
            Loras: ParseLoras(clipObj),
            PromptWindows: SortWindows(tags.ClipWindows.GetValueOrDefault(clipIndex)),
            BoundaryOut: boundaryOut
        );
    }

    private static string NormalizeBoundaryOut(string raw)
    {
        string compact = StringUtils.Compact(raw);
        if (compact.Length == 0)
        {
            return Constants.BoundaryOutCut;
        }
        if (StringUtils.Equals(compact, Constants.BoundaryOutContinue))
        {
            return Constants.BoundaryOutContinue;
        }
        if (StringUtils.Equals(compact, Constants.BoundaryOutCrossfade))
        {
            return Constants.BoundaryOutCrossfade;
        }
        return Constants.BoundaryOutCut;
    }

    private static IReadOnlyList<PromptWindowSpec> SortWindows(List<PromptWindowSpec> windows) =>
        windows is not { Count: > 0 } ? [] : [.. windows.OrderBy(w => w.Start)];

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
        UploadedAudioSpec embeddedImage = GetEmbeddedUploadSpec(refObj, UploadContainers.RefImage);
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
        JToken token = GetToken(obj, key);
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }
        if (token.Type == JTokenType.Integer)
        {
            return token.Value<int>();
        }
        string raw = $"{token}";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    private static List<JObject> GetObjectArray(JObject obj, string key) =>
        JsonUtil.Get(obj, key) is JArray array ? [.. array.OfType<JObject>()] : [];

    private static JObject GetObject(JObject obj, string key) =>
        JsonUtil.Get(obj, key) as JObject;

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
        string json = VideoStagesPromptSection.IsActive(g) ? VideoStagesPromptSection.GetDataJson(g) : null;
        if (string.IsNullOrWhiteSpace(json))
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
        (int steps, double cfgScale, string sampler, string scheduler) = StageDefaultsProvider.ReadHostDefaults(g);
        return new StageDefaults(
            Control: NormalizeControl(DefaultControl),
            Upscale: NormalizeUpscale(DefaultUpscale),
            UpscaleMethod: DefaultUpscaleMethod,
            Steps: steps,
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
            Steps: Math.Max(1, GetOptionalInt(stage, "Steps", defaults.Steps, locationPrefix)),
            CfgScale: NormalizeCfgScale(GetOptionalDouble(stage, "CfgScale", defaults.CfgScale, locationPrefix)),
            Sampler: GetOptionalString(stage, "Sampler", defaults.Sampler, locationPrefix, allowEmpty: false),
            Scheduler: GetOptionalString(stage, "Scheduler", defaults.Scheduler, locationPrefix, allowEmpty: false),
            ImageReference: NormalizeImageReference(GetString(stage, "ImageReference"), clipIndex, index, isTextToVideoRootWorkflow),
            ControlNetStrength: ParseStageControlNetStrength(stage, locationPrefix),
            ImageRefStrengths: ParseStageRefStrengths(stage, clipRefCount),
            ImageRefWasExplicit: JsonHasOwnProperty(stage, "ImageReference"),
            Loras: ParseLoras(stage)
        );
    }

    private static double? ParseStageControlNetStrength(JObject stage, string locationPrefix)
    {
        if (!JsonHasOwnProperty(stage, "ControlNetStrength"))
        {
            return null;
        }

        double value = GetOptionalDouble(stage, "ControlNetStrength", Constants.DefaultStageControlNetStrength, locationPrefix);
        return ClampUnitOrDefault(value, Constants.DefaultStageControlNetStrength);
    }

    // Clamp a finite value into [0, 1]; substitute the fallback for NaN/Infinity.
    private static double ClampUnitOrDefault(double value, double fallback) =>
        IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : fallback;

    private static IReadOnlyList<double> ParseStageRefStrengths(JObject stage, int clipRefCount)
    {
        if (clipRefCount <= 0)
        {
            return [];
        }

        double defaultStrength = Constants.DefaultStageRefStrength;
        List<double> strengths = [];
        if (JsonUtil.Get(stage, "refStrengths") is JArray array)
        {
            foreach (JToken entry in array)
            {
                if (entry.Type == JTokenType.Float || entry.Type == JTokenType.Integer)
                {
                    strengths.Add(ClampRefStrength(entry.Value<double>()));
                }
                else if (entry.Type == JTokenType.String
                    && double.TryParse($"{entry}".Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    strengths.Add(ClampRefStrength(parsed));
                }
            }
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

    private static double ClampRefStrength(double value) =>
        ClampUnitOrDefault(value, Constants.DefaultStageRefStrength);

    private static IReadOnlyList<LoraRef> ParseLoras(JObject obj)
    {
        List<LoraRef> loras = [];
        foreach (JObject entry in GetObjectArray(obj, "Loras"))
        {
            string name = GetString(entry, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            double weight = SanitizeLoraWeight(GetOptionalDouble(entry, "Weight", 1.0, "Lora"), 1.0);
            double? tencWeight = null;
            if (JsonHasOwnProperty(entry, "TextEncoderWeight"))
            {
                tencWeight = SanitizeLoraWeight(GetOptionalDouble(entry, "TextEncoderWeight", weight, "Lora"), weight);
            }
            loras.Add(new LoraRef(name.Trim(), weight, tencWeight));
        }
        return loras;
    }

    private static double SanitizeLoraWeight(double value, double fallback) =>
        IsFinite(value) ? value : fallback;

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

    private static string NormalizeOptionalModelName(string rawModelName)
    {
        return string.IsNullOrWhiteSpace(rawModelName) ? "" : rawModelName.Trim();
    }

    private static IReadOnlyList<IcLoraSpec> ParseIcLoras(JObject clipObj)
    {
        List<IcLoraSpec> entries = [];
        foreach (JObject entryObj in GetObjectArray(clipObj, UploadContainers.IcLorasCollection))
        {
            string lora = NormalizeControlNetLora(GetString(entryObj, "Lora"));
            if (lora.Length == 0)
            {
                continue;
            }
            entries.Add(new IcLoraSpec(
                Lora: lora,
                Preset: GetString(entryObj, "Preset")?.Trim(),
                Stage: Math.Max(-1,
                    (int)GetOptionalDouble(entryObj, "Stage", -1, "Clip IcLora")),
                Source: NormalizeIcLoraSource(GetString(entryObj, "Source")),
                Strength: Math.Clamp(
                    GetOptionalDouble(entryObj, "Strength", 1, "Clip IcLora"), 0, 5),
                AttentionStrength: Math.Clamp(
                    GetOptionalDouble(entryObj, "AttentionStrength", 1, "Clip IcLora"), 0, 1),
                ControlType: NormalizeIcLoraControlType(GetString(entryObj, "ControlType")),
                Video: GetEmbeddedUploadSpec(entryObj, UploadContainers.IcLoraVideo),
                DriveAudioRef: GetOptionalBool(entryObj, "DriveAudioRef", false)));
        }
        if (entries.Count == 0)
        {
            // Legacy single-entry form: ControlNetLora + ControlNetSource on the clip itself.
            string legacyLora = NormalizeControlNetLora(GetString(clipObj, "ControlNetLora"));
            if (legacyLora.Length > 0)
            {
                entries.Add(new IcLoraSpec(
                    Lora: legacyLora,
                    Source: NormalizeControlNetSource(GetString(clipObj, "ControlNetSource")),
                    Strength: 1,
                    AttentionStrength: 1,
                    ControlType: Constants.IcLoraControlNone,
                    Video: null));
            }
        }
        return entries;
    }

    private static string NormalizeIcLoraSource(string source)
    {
        string compact = StringUtils.Compact(source);
        if (compact.Length == 0 || StringUtils.Equals(compact, Constants.IcLoraSourceUpload))
        {
            return Constants.IcLoraSourceUpload;
        }
        if (StringUtils.Equals(compact, StringUtils.Compact(Constants.IcLoraSourceStageInput)))
        {
            return Constants.IcLoraSourceStageInput;
        }
        return NormalizeControlNetSource(source);
    }

    private static string NormalizeIcLoraControlType(string controlType)
    {
        string compact = StringUtils.Compact(controlType);
        if (StringUtils.Equals(compact, Constants.IcLoraControlCanny))
        {
            return Constants.IcLoraControlCanny;
        }
        if (StringUtils.Equals(compact, Constants.IcLoraControlDepth))
        {
            return Constants.IcLoraControlDepth;
        }
        if (StringUtils.Equals(compact, Constants.IcLoraControlNormal))
        {
            return Constants.IcLoraControlNormal;
        }
        return Constants.IcLoraControlNone;
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
        JToken token = GetToken(obj, key);
        if (token is null || token.Type == JTokenType.Null)
        {
            return defaultValue;
        }
        if (token.Type == JTokenType.Integer)
        {
            return token.Value<int>();
        }
        string raw = $"{token}";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
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
        JToken token = GetToken(obj, key);
        if (token is null || token.Type == JTokenType.Null)
        {
            return defaultValue;
        }
        if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
        {
            return token.Value<double>();
        }
        string raw = $"{token}";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            Logs.Warning(
                $"VideoStages: {locationPrefix} has invalid numeric field '{key}' value '{raw}'. "
                + $"Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    private static JToken GetToken(JObject obj, string key) => JsonUtil.Get(obj, key);

    private static string GetString(JObject obj, string key)
    {
        JToken token = JsonUtil.Get(obj, key);
        if (token is null)
        {
            return null;
        }
        return token.Type == JTokenType.Null ? null : $"{token}";
    }

    private static bool JsonHasOwnProperty(JObject obj, string key) =>
        JsonUtil.Get(obj, key) is not null;

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
