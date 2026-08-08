using System.Globalization;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Authoring;

internal static class RequestReader
{
    private const double DefaultControl = 0.5;
    private const double FirstStageControl = 1.0;
    private const double DefaultUpscale = 1.0;
    private const string DefaultUpscaleMethod = "pixel-lanczos";

    private sealed record StageDefaults(
        double Control,
        double Upscale,
        string UpscaleMethod,
        int Steps,
        double CfgScale,
        string Sampler,
        string Scheduler);

    private sealed record ClipReadContext(
        StageDefaults StageDefaults,
        bool IsTextToVideoRootWorkflow,
        int Fps,
        PromptTags.Directives Directives,
        Action<string> Warn);

    public static TimelineSpec Read(T2IParamInput input)
    {
        LegacyVideoSwapRequestSnapshot legacyVideoSwap = CaptureLegacyVideoSwap(input);
        Action<string> warn =
            warning => RequestWarnings.Track(input, warning);
        AuthoringDocument document = DocumentJson.Read(input);
        PromptTags.Directives directives = DocumentJson.IsActive(input)
            ? PromptTags.Read(
                input.Get(T2IParamTypes.Prompt, ""),
                input)
            : new PromptTags.Directives();

        (int? rawWidth, int? rawHeight, int? rawFps) = Overrides.ApplyTopLevel(
            directives, document.Width, document.Height, document.Fps, warn);
        Overrides.ApplyClipAndStage(document.Clips, directives, warn);

        int width = ResolveWidth(input, rawWidth);
        int height = ResolveHeight(input, rawHeight);
        int fps = ResolveFps(input, rawFps);
        bool isTextToVideo = RootHostWorkflowFacts.IsTextToVideoRootWorkflow(input);
        bool hasConfiguredResolution = rawWidth is > 0 && rawHeight is > 0;
        if (document.Clips.Count == 0)
        {
            return new TimelineSpec(
                width,
                height,
                fps,
                isTextToVideo,
                [],
                hasConfiguredResolution)
            {
                LegacyVideoSwap = legacyVideoSwap,
            };
        }

        ClipReadContext context = new(
            ReadDefaults(input),
            isTextToVideo,
            fps,
            directives,
            warn);

        List<ClipSpec> clips = [];
        int nextStageId = 0;
        for (int clipIndex = 0; clipIndex < document.Clips.Count; clipIndex++)
        {
            JObject clipObject = document.Clips[clipIndex];
            if (DocumentJson.GetOptionalBool(clipObject, "skipped", false))
            {
                break;
            }
            if (DocumentJson.GetArray(clipObject, "stages") is null)
            {
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: Entry {clipIndex} has no stages array and was ignored.");
                continue;
            }

            ClipSpec clip = ReadClip(
                clipObject,
                clipIndex,
                nextStageId,
                context);
            if (clip.Stages.Count == 0
                && clip.InitVideo is null
                && clip.AuthoredStages.Count == 0)
            {
                continue;
            }

            nextStageId += clip.Stages.Count;
            clips.Add(clip);
        }

        return new TimelineSpec(
            width,
            height,
            fps,
            isTextToVideo,
            clips,
            hasConfiguredResolution,
            AuthoringTimeline.ReadAudioSpans(
                document.AudioTracks,
                document.Clips,
                warn))
        {
            LegacyVideoSwap = legacyVideoSwap,
        };
    }

    private static ClipSpec ReadClip(
        JObject clipObject,
        int clipIndex,
        int firstStageId,
        ClipReadContext context)
    {
        string location = $"Clip {clipIndex}";
        double duration = DocumentJson.GetOptionalDouble(
            clipObject, "duration", 0, location, context.Warn);
        string audioSource = NormalizeAudioSource(
            DocumentJson.GetString(clipObject, "audioSource"));
        bool saveAudioTrack = DocumentJson.GetOptionalBool(
            clipObject, "saveAudioTrack", false);
        bool clipLengthFromAudio = DocumentJson.GetOptionalBool(
            clipObject, "clipLengthFromAudio", false);
        bool clipLengthFromControlNet = DocumentJson.GetOptionalBool(
            clipObject, "clipLengthFromControlNet", false);
        bool reuseAudio = DocumentJson.GetOptionalBool(
            clipObject, "reuseAudio", false);
        if (!double.IsFinite(duration) || duration < 0)
        {
            DocumentJson.Warn(
                context.Warn,
                $"VideoStages: {location} duration must be finite and non-negative; ignoring it.");
            duration = 0;
        }

        int? clipFrames = null;
        if (duration > 0)
        {
            try
            {
                clipFrames = AuthoringTimeline.CalculateStructuralFrameCount(
                    duration,
                    context.Fps);
            }
            catch (OverflowException)
            {
                DocumentJson.Warn(
                    context.Warn,
                    $"VideoStages: {location} duration at {context.Fps} fps exceeds the "
                        + "supported frame range and was ignored.");
            }
        }

        IReadOnlyList<IcLoraSpec> icLoras = Loras.ReadIc(clipObject, context.Warn);
        List<JObject> rawStages = DocumentJson.GetObjectArray(clipObject, "stages");
        IReadOnlyList<FrameRefSpec> frameRefs = FrameReferences.Read(
            clipObject,
            clipIndex,
            context.Warn);
        InitVideoSpec initVideo = AuthoringTimeline.ReadInitVideo(
            clipObject, duration, context.Fps, clipIndex, context.Warn);
        List<StageSpec> stages = ReadStages(
            rawStages,
            clipIndex,
            firstStageId,
            frameRefs.Count,
            initVideo is not null,
            context);
        ApplyRetake(stages, clipObject, clipIndex, duration, initVideo is not null, context);

        return new ClipSpec(
            Id: clipIndex,
            Frames: clipFrames,
            AudioSource: audioSource,
            IcLoras: icLoras,
            SaveAudioTrack: saveAudioTrack,
            ClipLengthFromAudio: clipLengthFromAudio,
            ClipLengthFromControlNet: clipLengthFromControlNet,
            ReuseAudio: reuseAudio,
            UploadedAudio: DocumentJson.GetEmbeddedUpload(
                clipObject, UploadContainers.ClipAudio),
            FrameRefs: frameRefs,
            Stages: stages,
            Loras: Loras.ReadNormal(clipObject, context.Warn),
            PromptWindows: SortWindows(context.Directives.ClipWindows.GetValueOrDefault(clipIndex)),
            BoundaryOut: NormalizeBoundaryOut(
                DocumentJson.GetString(clipObject, "boundaryOut")),
            BoundaryOutOverlap: Math.Max(
                0,
                DocumentJson.GetOptionalInt(
                    clipObject,
                    "boundaryOutOverlap",
                    0,
                    location,
                    context.Warn)),
            InitVideo: initVideo,
            BoundaryOutCarryAudio: DocumentJson.GetOptionalBool(
                clipObject,
                "boundaryOutCarryAudio",
                false),
            BoundaryOutReferenceScale: ReferenceScale.Normalize(
                DocumentJson.GetOptionalDouble(
                    clipObject,
                    "boundaryOutReferenceScale",
                    ReferenceScale.Full,
                    location,
                    context.Warn)),
            BoundaryOutReferenceIncludeSoundtrack: DocumentJson.GetOptionalBool(
                clipObject,
                "boundaryOutReferenceIncludeSoundtrack",
                true),
            ReferenceFraming: ReferenceFraming.Parse(
                DocumentJson.GetString(clipObject, "refFraming")),
            References: ClipReferences.Read(clipObject, clipIndex, context.Warn))
        {
            AuthoredArchitectureHint = DocumentJson.GetString(
                clipObject,
                "architectureHint")?.Trim().ToLowerInvariant(),
            AuthoredModelProfileHint = DocumentJson.GetString(
                clipObject,
                "modelProfileId")?.Trim().ToLowerInvariant(),
            AuthoredStages = ProjectAuthoredStages(rawStages),
        };
    }

    private static List<StageSpec> ReadStages(
        IReadOnlyList<JObject> rawStages,
        int clipIndex,
        int firstStageId,
        int frameRefCount,
        bool initVideoClip,
        ClipReadContext context)
    {
        List<StageSpec> stages = [];
        for (int stageIndex = 0; stageIndex < rawStages.Count; stageIndex++)
        {
            JObject stage = rawStages[stageIndex];
            if (DocumentJson.GetOptionalBool(stage, "skipped", false))
            {
                break;
            }
            StageSpec parsed = ReadStage(
                stage,
                clipIndex,
                stageId: firstStageId + stages.Count,
                rawStageIndex: stageIndex,
                clipStageIndex: stages.Count,
                defaults: context.StageDefaults,
                frameRefCount: frameRefCount,
                isTextToVideoRootWorkflow: context.IsTextToVideoRootWorkflow,
                initVideoClip: initVideoClip,
                warn: context.Warn);
            if (parsed is not null)
            {
                stages.Add(parsed);
            }
        }
        return stages;
    }

    private static StageSpec? ReadStage(
        JObject stage,
        int clipIndex,
        int stageId,
        int rawStageIndex,
        int clipStageIndex,
        StageDefaults defaults,
        int frameRefCount,
        bool isTextToVideoRootWorkflow,
        bool initVideoClip,
        Action<string> warn)
    {
        string location = $"Clip {clipIndex} stage {rawStageIndex}";
        string model = DocumentJson.GetString(stage, "model")?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            DocumentJson.Warn(
                warn,
                $"VideoStages: Clip {clipIndex} stage {rawStageIndex} has no model and was ignored.");
            return null;
        }

        double control = NormalizeControl(DocumentJson.GetOptionalDouble(
            stage, "control", defaults.Control, location, warn));
        double upscale = NormalizeUpscale(DocumentJson.GetOptionalDouble(
            stage, "upscale", defaults.Upscale, location, warn));
        string upscaleMethod = DocumentJson.GetOptionalString(
            stage, "upscaleMethod", defaults.UpscaleMethod, location, allowEmpty: false, warn);

        bool isGenerationFirstStage = clipStageIndex == 0 && !initVideoClip;
        if (isGenerationFirstStage)
        {
            control = FirstStageControl;
            upscale = DefaultUpscale;
            upscaleMethod = DefaultUpscaleMethod;
        }
        else
        {
            upscale = Math.Max(0.25, upscale);
        }

        return new StageSpec(
            Id: stageId,
            Control: control,
            Upscale: upscale,
            UpscaleMethod: upscaleMethod,
            Model: model,
            Steps: Math.Max(1, DocumentJson.GetOptionalInt(
                stage, "steps", defaults.Steps, location, warn)),
            CfgScale: NormalizeCfgScale(DocumentJson.GetOptionalDouble(
                stage, "cfgScale", defaults.CfgScale, location, warn)),
            Sampler: DocumentJson.GetOptionalString(
                stage, "sampler", defaults.Sampler, location, allowEmpty: false, warn),
            Scheduler: DocumentJson.GetOptionalString(
                stage, "scheduler", defaults.Scheduler, location, allowEmpty: false, warn),
            ImageReference: NormalizeImageReference(
                DocumentJson.GetString(stage, "imageReference"),
                clipIndex,
                rawStageIndex,
                clipStageIndex,
                isTextToVideoRootWorkflow,
                warn),
            ClipStageIndex: clipStageIndex,
            ClipStageRawIndex: rawStageIndex,
            ControlNetStrength: ReadControlNetStrength(stage, location, warn),
            IcLoraStrengths: ReadIcLoraStrengths(stage),
            FrameRefStrengths: ReadRefStrengths(stage, frameRefCount),
            ImageRefWasExplicit: DocumentJson.HasProperty(stage, "imageReference"),
            Loras: Loras.ReadNormal(stage, warn),
            LoraWeights: Loras.ReadWeights(stage));
    }

    private static StageDefaults ReadDefaults(T2IParamInput input)
    {
        int steps = input.TryGet(T2IParamTypes.VideoSteps, out int explicitVideoSteps)
            ? explicitVideoSteps
            : input.Get(T2IParamTypes.Steps, 8, autoFixDefault: true);
        double cfgScale = input.TryGet(T2IParamTypes.VideoCFG, out double explicitVideoCfg)
            ? explicitVideoCfg
            : input.Get(T2IParamTypes.CFGScale, 1, autoFixDefault: true);
        string sampler = ComfyUIBackendExtension.SamplerParam is null
            ? "euler"
            : input.Get(
                ComfyUIBackendExtension.SamplerParam,
                "euler",
                autoFixDefault: true);
        string scheduler = ComfyUIBackendExtension.SchedulerParam is null
            ? "normal"
            : input.Get(
                ComfyUIBackendExtension.SchedulerParam,
                "normal",
                autoFixDefault: true);

        return new StageDefaults(
            NormalizeControl(DefaultControl),
            NormalizeUpscale(DefaultUpscale),
            DefaultUpscaleMethod,
            Math.Max(1, steps),
            NormalizeCfgScale(cfgScale),
            sampler,
            scheduler);
    }

    private static LegacyVideoSwapRequestSnapshot CaptureLegacyVideoSwap(T2IParamInput input)
    {
        T2IModel swapModel = input.Get(T2IParamTypes.VideoSwapModel, null);
        bool hasExplicitPercent =
            input.TryGet(T2IParamTypes.VideoSwapPercent, out double swapPercent);
        bool hasSwapSectionOverrides =
            input.SectionParamOverrides.TryGetValue(
                T2IParamInput.SectionID_VideoSwap,
                out T2IParamSet swapSection)
            && swapSection.ValuesInput.Count > 0;
        return new(
            swapModel?.Name,
            hasExplicitPercent,
            hasExplicitPercent ? swapPercent : null,
            hasSwapSectionOverrides);
    }

    private static IReadOnlyList<AuthoredStageModelSpec> ProjectAuthoredStages(
        IReadOnlyList<JObject> rawStages)
    {
        int firstSkipped = -1;
        List<AuthoredStageModelSpec> authored = [];
        for (int rawIndex = 0; rawIndex < rawStages.Count; rawIndex++)
        {
            JObject stage = rawStages[rawIndex];
            if (firstSkipped < 0
                && DocumentJson.GetOptionalBool(stage, "skipped", false))
            {
                firstSkipped = rawIndex;
            }
            string model = DocumentJson.GetString(stage, "model");
            authored.Add(new(
                rawIndex,
                model,
                DocumentJson.GetString(stage, "modelProfileId")?.Trim().ToLowerInvariant(),
                firstSkipped >= 0 || string.IsNullOrWhiteSpace(model)));
        }
        return authored;
    }

    private static void ApplyRetake(
        List<StageSpec> stages,
        JObject clipObject,
        int clipIndex,
        double duration,
        bool hasInitVideo,
        ClipReadContext context)
    {
        if (!hasInitVideo || stages.Count == 0)
        {
            return;
        }

        RetakeWindowSpec retake = AuthoringTimeline.ReadRetake(
            clipObject, context.Fps, clipIndex, duration, context.Warn);
        if (retake is not null)
        {
            stages[^1] = stages[^1] with { RetakeWindow = retake };
        }
    }

    private static double? ReadControlNetStrength(
        JObject stage,
        string location,
        Action<string> warn)
    {
        if (!DocumentJson.HasProperty(stage, "controlNetStrength"))
        {
            return null;
        }
        double value = DocumentJson.GetOptionalDouble(
            stage,
            "controlNetStrength",
            Constants.DefaultStageControlNetStrength,
            location,
            warn);
        return ClampUnitOrDefault(value, Constants.DefaultStageControlNetStrength);
    }

    private static IReadOnlyList<double> ReadIcLoraStrengths(JObject stage)
    {
        if (DocumentJson.GetArray(stage, "icLoraStrengths") is not JArray array)
        {
            return [];
        }
        List<double> strengths = [];
        foreach (JToken entry in array)
        {
            if (entry.Type is JTokenType.Float or JTokenType.Integer)
            {
                strengths.Add(ClampUnitOrDefault(
                    entry.Value<double>(),
                    Constants.DefaultStageControlNetStrength));
            }
            else if (entry.Type == JTokenType.String
                && double.TryParse(
                    $"{entry}".Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsed))
            {
                strengths.Add(ClampUnitOrDefault(
                    parsed,
                    Constants.DefaultStageControlNetStrength));
            }
        }
        return strengths.AsReadOnly();
    }

    private static IReadOnlyList<double> ReadRefStrengths(JObject stage, int frameRefCount)
    {
        if (frameRefCount <= 0)
        {
            return [];
        }

        List<double> strengths = [];
        if (DocumentJson.GetArray(stage, "frameRefStrengths") is JArray array)
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

        while (strengths.Count < frameRefCount)
        {
            strengths.Add(Constants.DefaultStageRefStrength);
        }
        if (strengths.Count > frameRefCount)
        {
            strengths.RemoveRange(frameRefCount, strengths.Count - frameRefCount);
        }
        return strengths;
    }

    private static string NormalizeImageReference(
        string rawValue,
        int clipIndex,
        int rawStageIndex,
        int stageIndex,
        bool isTextToVideoRootWorkflow,
        Action<string> warn)
    {
        if (isTextToVideoRootWorkflow)
        {
            if (!string.IsNullOrWhiteSpace(rawValue)
                && StageGuideReference.Classify(rawValue).Kind
                    != StageGuideReferenceKind.Generated)
            {
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: Clip {clipIndex} stage {rawStageIndex} uses ImageReference '{rawValue}' on a text-to-video workflow. "
                    + $"Using '{MediaSource.Generated}' instead.");
            }
            return MediaSource.Generated;
        }

        string defaultReference = stageIndex == 0
            ? MediaSource.Generated
            : MediaSource.PreviousStage;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaultReference;
        }

        StageGuideReferenceSelection selection = StageGuideReference.Classify(rawValue);
        switch (selection)
        {
            case { Kind: StageGuideReferenceKind.Generated }:
                return MediaSource.Generated;
            case { Kind: StageGuideReferenceKind.Base }:
                return MediaSource.Base;
            case { Kind: StageGuideReferenceKind.Refiner }:
                return MediaSource.Refiner;
            case { Kind: StageGuideReferenceKind.PreviousStage }:
                // Stage 0 has no previous stage, so defaultReference collapses it to Generated.
                return defaultReference;
            case { Kind: StageGuideReferenceKind.Base2Edit, ReferencedStageIndex: int edit }:
                return MediaSource.FormatBase2Edit(edit);
            case
            {
                Kind: StageGuideReferenceKind.ExplicitStage,
                ReferencedStageIndex: int explicitStage,
            } when explicitStage < stageIndex:
                return MediaSource.FormatExplicitStageIndex(explicitStage);
            case { Kind: StageGuideReferenceKind.ExplicitStage }:
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: Clip {clipIndex} stage {rawStageIndex} has invalid ImageReference '{rawValue}' "
                    + $"(must reference a strictly previous stage). Using '{defaultReference}' instead.");
                return defaultReference;
            default:
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: Clip {clipIndex} stage {rawStageIndex} has invalid ImageReference '{rawValue}'. "
                    + $"Using '{defaultReference}' instead.");
                return defaultReference;
        }
    }

    private static int ResolveWidth(T2IParamInput input, int? authoredWidth) =>
        authoredWidth is > 0 ? authoredWidth.Value : input.GetImageWidth();

    private static int ResolveHeight(T2IParamInput input, int? authoredHeight) =>
        authoredHeight is > 0 ? authoredHeight.Value : input.GetImageHeight();

    private static int ResolveFps(T2IParamInput input, int? authoredFps)
    {
        if (authoredFps is > 0)
        {
            return authoredFps.Value;
        }
        return input.TryGet(T2IParamTypes.VideoFPS, out int videoFps) && videoFps > 0
            ? videoFps
            : 24;
    }

    private static string NormalizeAudioSource(string audioSource) =>
        string.IsNullOrWhiteSpace(audioSource)
            ? MediaSource.Native
            : audioSource.Trim();

    private static IReadOnlyList<PromptWindowSpec> SortWindows(List<PromptWindowSpec> windows) =>
        windows is not { Count: > 0 } ? [] : [.. windows.OrderBy(window => window.Start)];

    private static double ClampUnitOrDefault(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : fallback;

    private static string NormalizeBoundaryOut(string raw)
    {
        string compact = StringUtils.Compact(raw);
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
}
