using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>
/// Compiles the parsed VideoStages specification into a deterministic LTX execution plan. This is
/// a pure transformation: it neither inspects nor mutates the host workflow or its graph.
/// </summary>
internal static class VideoExecutionPlanCompiler
{
    public static VideoExecutionPlan Compile(VideoStagesSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Compile(spec, RootEnvironment.FromSpec(spec));
    }

    public static VideoExecutionPlan Compile(VideoStagesSpec spec, RootEnvironment rootEnvironment)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(rootEnvironment);

        List<VideoPlanDiagnostic> diagnostics = [];
        IReadOnlyList<ClipSpec> executableClips = (spec.Clips ?? []).Where(IsExecutableClip).ToArray();
        if (executableClips.Count != (spec.Clips?.Count ?? 0))
        {
            diagnostics.Add(new VideoPlanDiagnostic(
                VideoPlanDiagnosticSeverity.Warning,
                "inactive-clips-ignored",
                "Clips without a source video or active stages were ignored by the execution plan."));
        }
        List<ClipSpec> activeClips = [];
        HashSet<int> seenClipIds = [];
        foreach (ClipSpec clip in executableClips)
        {
            if (!seenClipIds.Add(clip.Id))
            {
                diagnostics.Add(new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Error,
                    "duplicate-clip-id",
                    $"Clip id {clip.Id} is duplicated; only its first occurrence is planned.",
                    clip.Id));
                continue;
            }
            activeClips.Add(clip);
        }

        RootPlan root = BuildRootPlan(rootEnvironment, activeClips);
        List<BoundaryPlan> boundaries = BuildBoundaries(activeClips, diagnostics);
        List<ClipPlan> clips = [];
        int totalStageCount = activeClips.Sum(clip => clip.Stages?.Count ?? 0);
        int firstStageOrdinal = 0;
        for (int i = 0; i < activeClips.Count; i++)
        {
            bool usesIncomingContinuity = i > 0
                && boundaries[i - 1].Effective == BoundaryExecutionMode.Continue;
            clips.Add(BuildClipPlan(
                activeClips[i],
                spec.IsTextToVideo,
                spec.Width,
                spec.Height,
                spec.FPS,
                usesIncomingContinuity,
                i == activeClips.Count - 1,
                activeClips.Count > 1,
                totalStageCount,
                firstStageOrdinal));
            firstStageOrdinal += activeClips[i].Stages?.Count ?? 0;
        }

        VideoExecutionPlan plan = new(
            spec.Width,
            spec.Height,
            spec.FPS,
            VideoModelFamily.Ltx,
            root,
            Array.AsReadOnly(clips.ToArray()),
            Array.AsReadOnly(boundaries.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()));
        return plan with { AudioTimeline = AudioTimelinePlanCompiler.Compile(plan) };
    }

    private static bool IsExecutableClip(ClipSpec clip) =>
        clip is not null && (clip.SourceVideo is not null || clip.Stages is { Count: > 0 });

    private static RootPlan BuildRootPlan(RootEnvironment environment, IReadOnlyList<ClipSpec> clips)
    {
        if (clips.Count == 0)
        {
            return new RootPlan(environment.HostKind, RootUse.None, HostCoreDisposition.Keep,
                TimelineOutputDisposition.PreserveHostOutput, NativeAudioDisposition.KeepHostAudio);
        }

        bool hasGeneratedClip = clips.Any(clip => clip.SourceVideo is null);
        bool sourcedLeadWithGeneratedClips = clips[0].SourceVideo is not null && hasGeneratedClip;
        if (environment.HasGlobalRefineSource)
        {
            return new RootPlan(
                HostRootKind.GlobalRefineSource,
                RootUse.GlobalRefineReplacement,
                environment.CanHandoffHostCore ? HostCoreDisposition.Handoff : HostCoreDisposition.Drop,
                TimelineOutputDisposition.PublishTimelineOutput,
                NativeAudioDisposition.UseGlobalRefineAudio);
        }
        return new RootPlan(
            environment.HostKind,
            environment.HostKind == HostRootKind.TextToVideoRoot
                ? RootUse.Discard
                : !hasGeneratedClip ? RootUse.Discard
                : sourcedLeadWithGeneratedClips ? RootUse.GeneratedClipDonor : RootUse.ClipZeroSeed,
            environment.HostKind == HostRootKind.TextToVideoRoot || !hasGeneratedClip
                ? HostCoreDisposition.Drop
                : environment.CanHandoffHostCore ? HostCoreDisposition.Handoff : HostCoreDisposition.Keep,
            TimelineOutputDisposition.PublishTimelineOutput,
            environment.HostKind == HostRootKind.TextToVideoRoot || !hasGeneratedClip
                ? NativeAudioDisposition.DiscardWithRoot
                : NativeAudioDisposition.MakeAvailableToTimeline);
    }

    private static ClipPlan BuildClipPlan(
        ClipSpec clip,
        bool isTextToVideo,
        int width,
        int height,
        int framesPerSecond,
        bool usesIncomingContinuity,
        bool isLastClip,
        bool isMultiClip,
        int totalStageCount,
        int firstStageOrdinal)
    {
        bool sourced = clip.SourceVideo is not null;
        AudioPlan audio = AudioPlanCompiler.Compile(clip);
        ClipInputKind clipInput = sourced
            ? ClipInputKind.SourceVideo
            : isTextToVideo ? ClipInputKind.EmptyLatent : ClipInputKind.RootMedia;
        PromptRelayPlan promptRelay = CompilePromptRelay(clip, framesPerSecond);
        List<StagePlan> stages = [];
        for (int i = 0; i < (clip.Stages?.Count ?? 0); i++)
        {
            StageSpec stage = clip.Stages[i];
            bool isClipTerminal = i == clip.Stages.Count - 1;
            stages.Add(new StagePlan(
                stage.Id,
                stage.ClipStageIndex,
                stage.ClipStageRawIndex,
                ResolveStageInput(clipInput, i),
                ResolveExecution(clip, stage, i, clipInput),
                new StageCorePlan(
                    stage.Model,
                    stage.Control,
                    stage.Steps,
                    stage.CfgScale,
                    stage.Sampler,
                    stage.Scheduler,
                    stage.ControlNetStrength,
                    stage.ImageRefWasExplicit),
                CompileGuide(stage.ImageReference),
                CompileUpscale(stage),
                CompileNormalLoras(clip, stage),
                CompileIcLoras(clip, stage),
                CompileRetake(stage.RetakeWindow),
                promptRelay,
                CompileImageReferences(clip, stage),
                CompileAudioAction(audio, stage),
                new StageOutputPlan(
                    isClipTerminal,
                    IsTimelineTerminal: isClipTerminal && isLastClip && !isMultiClip,
                    FeedsClipAssembly: isClipTerminal && isMultiClip,
                    firstStageOrdinal + i < totalStageCount - 1
                        ? IntermediateOutputPolicy.ControlledByHostSetting
                        : IntermediateOutputPolicy.NotEligible,
                    clip.SaveAudioTrack)));
        }

        return new ClipPlan(
            clip.Id,
            clip.Frames,
            clipInput,
            sourced,
            CompileSourceVideo(clip.SourceVideo, clip.Frames, width, height, framesPerSecond),
            Array.AsReadOnly(stages.ToArray()),
            audio,
            UsesIncomingContinuity: usesIncomingContinuity);
    }

    private static SourceVideoPlan CompileSourceVideo(
        SourceVideoSpec source,
        int? targetFrames,
        int width,
        int height,
        int framesPerSecond) => source is null
            ? null
            : new SourceVideoPlan(
                source.Data,
                source.FileName,
                source.StartSeconds,
                targetFrames,
                width,
                height,
                framesPerSecond);

    private static StageInputKind ResolveStageInput(ClipInputKind clipInput, int stageIndex)
    {
        if (stageIndex > 0)
        {
            return StageInputKind.PreviousStage;
        }
        return clipInput switch
        {
            ClipInputKind.EmptyLatent => StageInputKind.EmptyLatent,
            ClipInputKind.SourceVideo => StageInputKind.SourceVideo,
            _ => StageInputKind.RootMedia,
        };
    }

    private static StageExecutionMode ResolveExecution(
        ClipSpec clip,
        StageSpec stage,
        int stageIndex,
        ClipInputKind clipInput)
    {
        if (stage.RetakeWindow is not null)
        {
            return StageExecutionMode.Retake;
        }
        if (stage.IsPassthrough)
        {
            return StageExecutionMode.Passthrough;
        }
        if (stageIndex == 0 && clip.SourceVideo is null && clipInput == ClipInputKind.EmptyLatent)
        {
            return StageExecutionMode.GenerateFromEmptyLatent;
        }
        if (stageIndex == 0 && clip.SourceVideo is null)
        {
            return StageExecutionMode.GenerateOrRefineFromRootMedia;
        }
        return StageExecutionMode.Refine;
    }

    private static StageUpscalePlan CompileUpscale(StageSpec stage)
    {
        StageUpscaleMode mode;
        if (stage.Upscale == 1)
        {
            mode = StageUpscaleMode.None;
        }
        else if (stage.IsPixelUpscale)
        {
            mode = StageUpscaleMode.Pixel;
        }
        else if (stage.IsModelUpscale)
        {
            mode = StageUpscaleMode.Model;
        }
        else if (stage.IsLatentUpscale)
        {
            mode = StageUpscaleMode.Latent;
        }
        else if (stage.IsLatentModelUpscale)
        {
            mode = StageUpscaleMode.LatentModel;
        }
        else
        {
            mode = StageUpscaleMode.Unsupported;
        }

        string raw = stage.UpscaleMethod?.Trim() ?? "";
        int separator = raw.IndexOf('-');
        string methodName = separator >= 0 && separator < raw.Length - 1
            ? raw[(separator + 1)..]
            : raw;
        return new StageUpscalePlan(mode, stage.Upscale, raw, methodName);
    }

    private static GuideReferencePlan CompileGuide(string rawValue)
    {
        string raw = rawValue?.Trim() ?? "";
        if (StringUtils.Equals(raw, "Base"))
        {
            return new(GuideReferenceKind.Base, raw, null);
        }
        if (StringUtils.Equals(raw, "Refiner"))
        {
            return new(GuideReferenceKind.Refiner, raw, null);
        }
        if (StringUtils.Equals(raw, "Generated"))
        {
            return new(GuideReferenceKind.Generated, raw, null);
        }
        if (StringUtils.Equals(raw, "PreviousStage"))
        {
            return new(GuideReferenceKind.PreviousStage, raw, null);
        }
        if (ImageReference.TryParseExplicitStageIndex(raw, out int stageIndex))
        {
            return new(GuideReferenceKind.ExplicitStage, raw, stageIndex);
        }
        if (ImageReference.TryParseBase2EditStageIndex(raw, out int editStageIndex))
        {
            return new(GuideReferenceKind.Base2Edit, raw, editStageIndex);
        }
        return new(GuideReferenceKind.Unknown, raw, null);
    }

    private static ImmutableArray<NormalLoraPlan> CompileNormalLoras(ClipSpec clip, StageSpec stage)
    {
        ImmutableArray<NormalLoraPlan>.Builder plans = ImmutableArray.CreateBuilder<NormalLoraPlan>();
        AppendLoras(plans, clip.Loras, NormalLoraScope.Clip);
        AppendLoras(plans, stage.Loras, NormalLoraScope.Stage);
        return plans.ToImmutable();
    }

    private static void AppendLoras(
        ImmutableArray<NormalLoraPlan>.Builder plans,
        IReadOnlyList<LoraRef> entries,
        NormalLoraScope scope)
    {
        foreach (LoraRef entry in entries ?? [])
        {
            plans.Add(new NormalLoraPlan(
                plans.Count,
                scope,
                entry.Name,
                entry.Weight,
                entry.TencWeight ?? entry.Weight,
                entry.TencWeight));
        }
    }

    private static ImmutableArray<IcLoraPlan> CompileIcLoras(ClipSpec clip, StageSpec stage)
    {
        ImmutableArray<IcLoraPlan>.Builder plans = ImmutableArray.CreateBuilder<IcLoraPlan>();
        IReadOnlyList<IcLoraSpec> entries = clip.IcLoras ?? [];
        for (int i = 0; i < entries.Count; i++)
        {
            IcLoraSpec entry = entries[i];
            if (entry.Stage >= 0 && entry.Stage != stage.ClipStageRawIndex)
            {
                continue;
            }

            IcLoraDrivePlan drive = CompileIcLoraDrive(clip, entry);
            IcLoraGuideStrengthSource guideStrengthSource = IcLoraGuideStrengthSource.NotApplicable;
            double? guideStrength = null;
            if (drive.HasDriveMedia)
            {
                if (stage.ControlNetStrength is double stageStrength)
                {
                    guideStrengthSource = IcLoraGuideStrengthSource.StageOverride;
                    guideStrength = stageStrength;
                }
                else if (drive.Kind == IcLoraDriveSourceKind.ControlNet)
                {
                    guideStrengthSource = IcLoraGuideStrengthSource.ControlNetSlot;
                }
                else
                {
                    guideStrengthSource = IcLoraGuideStrengthSource.DefaultOne;
                    guideStrength = 1.0;
                }
            }

            plans.Add(new IcLoraPlan(
                i,
                entry.Lora,
                StringUtils.Equals(entry.Lora, Constants.IcLoraAutoModel),
                entry.Preset,
                entry.Strength,
                entry.AttentionStrength,
                CompileIcLoraControl(entry.ControlType),
                drive,
                guideStrengthSource,
                guideStrength,
                entry.DriveAudioRef,
                entry.Stage < 0,
                entry.Stage < 0 ? null : entry.Stage));
        }
        return plans.ToImmutable();
    }

    private static IcLoraDrivePlan CompileIcLoraDrive(ClipSpec clip, IcLoraSpec entry)
    {
        string raw = entry.Source?.Trim() ?? "";
        if (StringUtils.Equals(raw, Constants.IcLoraSourceUpload))
        {
            if (!string.IsNullOrWhiteSpace(entry.Video?.Data))
            {
                string data = entry.Video.Data;
                IcLoraUploadedMediaKind mediaKind = data.StartsWith(
                    "data:image/", StringComparison.OrdinalIgnoreCase)
                    ? IcLoraUploadedMediaKind.Image
                    : data.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase)
                        ? IcLoraUploadedMediaKind.Video
                        : IcLoraUploadedMediaKind.Unknown;
                return new(
                    IcLoraDriveSourceKind.UploadedMedia,
                    raw,
                    null,
                    mediaKind,
                    entry.Video.FileName,
                    data,
                    HasDriveMedia: true);
            }
            if (clip.SourceVideo is not null)
            {
                return new(
                    IcLoraDriveSourceKind.SourcedClipInput,
                    raw,
                    null,
                    IcLoraUploadedMediaKind.None,
                    null,
                    null,
                    HasDriveMedia: true);
            }
            return new(
                IcLoraDriveSourceKind.LoaderOnly,
                raw,
                null,
                IcLoraUploadedMediaKind.None,
                null,
                null,
                HasDriveMedia: false);
        }
        if (StringUtils.Equals(raw, Constants.IcLoraSourceStageInput))
        {
            return new(
                IcLoraDriveSourceKind.StageInput,
                raw,
                null,
                IcLoraUploadedMediaKind.None,
                null,
                null,
                HasDriveMedia: true);
        }
        if (TryParseControlNetIndex(raw, out int controlNetIndex))
        {
            return new(
                IcLoraDriveSourceKind.ControlNet,
                raw,
                controlNetIndex,
                IcLoraUploadedMediaKind.None,
                null,
                null,
                HasDriveMedia: true);
        }
        return new(
            IcLoraDriveSourceKind.Unknown,
            raw,
            null,
            IcLoraUploadedMediaKind.None,
            null,
            null,
            HasDriveMedia: false);
    }

    private static bool TryParseControlNetIndex(string source, out int index)
    {
        string compact = StringUtils.Compact(source);
        if (compact.StartsWith("ControlNet", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(compact.AsSpan("ControlNet".Length), out int oneBased)
            && oneBased is >= 1 and <= 3)
        {
            index = oneBased - 1;
            return true;
        }
        index = -1;
        return false;
    }

    private static IcLoraControlMode CompileIcLoraControl(string controlType)
    {
        if (StringUtils.Equals(controlType, Constants.IcLoraControlNone))
        {
            return IcLoraControlMode.None;
        }
        if (StringUtils.Equals(controlType, Constants.IcLoraControlCanny))
        {
            return IcLoraControlMode.Canny;
        }
        if (StringUtils.Equals(controlType, Constants.IcLoraControlDepth))
        {
            return IcLoraControlMode.Depth;
        }
        if (StringUtils.Equals(controlType, Constants.IcLoraControlNormal))
        {
            return IcLoraControlMode.Normal;
        }
        return IcLoraControlMode.Unknown;
    }

    private static RetakePlan CompileRetake(RetakeWindowSpec retake) => retake is null
        ? null
        : new RetakePlan(
            retake.StartFrame,
            retake.LengthFrames,
            retake.StartFrame + Math.Max(0, retake.LengthFrames),
            retake.Strength);

    private static PromptRelayPlan CompilePromptRelay(ClipSpec clip, int framesPerSecond)
    {
        ImmutableArray<PromptWindowPlan> windows = (clip.PromptWindows ?? [])
            .Where(window => window is not null
                && !string.IsNullOrWhiteSpace(window.Prompt)
                && window.Duration > 0)
            .OrderBy(window => window.Start)
            .Select(window => new PromptWindowPlan(
                window.Prompt.Trim(),
                window.Start,
                window.Duration,
                window.Start + window.Duration))
            .ToImmutableArray();
        if (windows.IsEmpty)
        {
            return new(PromptRelayMode.None, windows, []);
        }
        if (clip.Frames is not int frames || framesPerSecond <= 0)
        {
            return new(PromptRelayMode.RequiresRuntimeLength, windows, []);
        }

        ImmutableArray<PromptRelaySegmentPlan> segments =
            TilePromptWindows(windows, frames / (double)framesPerSecond);
        PromptRelayMode mode = segments.Length switch
        {
            0 => PromptRelayMode.None,
            1 when !string.IsNullOrWhiteSpace(segments[0].Prompt) =>
                PromptRelayMode.SinglePromptOverride,
            >= 2 => PromptRelayMode.Relay,
            _ => PromptRelayMode.None,
        };
        return new(mode, windows, segments);
    }

    private static ImmutableArray<PromptRelaySegmentPlan> TilePromptWindows(
        ImmutableArray<PromptWindowPlan> windows,
        double clipSeconds)
    {
        const double epsilon = 1e-4;
        double total = Math.Max(0, clipSeconds);
        double cursor = 0;
        ImmutableArray<PromptRelaySegmentPlan>.Builder segments =
            ImmutableArray.CreateBuilder<PromptRelaySegmentPlan>();
        foreach (PromptWindowPlan window in windows)
        {
            double start = Math.Clamp(window.StartSeconds, 0, total);
            double end = Math.Clamp(window.EndSeconds, start, total);
            if (start > cursor + epsilon)
            {
                segments.Add(new("", start - cursor));
                cursor = start;
            }
            if (end > cursor + epsilon)
            {
                segments.Add(new(window.Prompt, end - cursor));
                cursor = end;
            }
        }
        if (total - cursor > epsilon)
        {
            segments.Add(new("", total - cursor));
        }
        return segments.ToImmutable();
    }

    private static ImmutableArray<ImageReferencePlan> CompileImageReferences(
        ClipSpec clip,
        StageSpec stage)
    {
        ImmutableArray<ImageReferencePlan>.Builder plans =
            ImmutableArray.CreateBuilder<ImageReferencePlan>();
        IReadOnlyList<ImageRefSpec> references = clip.ImageRefs ?? [];
        IReadOnlyList<double> strengths = stage.ImageRefStrengths ?? [];
        for (int i = 0; i < references.Count; i++)
        {
            ImageRefSpec reference = references[i];
            (ImageReferenceSourceKind sourceKind, int? editStage) =
                CompileImageReferenceSource(reference.Source);
            plans.Add(new ImageReferencePlan(
                i,
                sourceKind,
                reference.Source?.Trim() ?? "",
                editStage,
                reference.Frame,
                reference.FromEnd ? ImageReferenceFrameOrigin.End : ImageReferenceFrameOrigin.Start,
                i < strengths.Count ? strengths[i] : Constants.DefaultStageRefStrength,
                reference.UploadFileName,
                reference.Data));
        }
        return plans.ToImmutable();
    }

    private static (ImageReferenceSourceKind Kind, int? EditStage) CompileImageReferenceSource(
        string rawSource)
    {
        if (StringUtils.Equals(rawSource, "Upload"))
        {
            return (ImageReferenceSourceKind.Upload, null);
        }
        if (StringUtils.Equals(rawSource, "Base"))
        {
            return (ImageReferenceSourceKind.Base, null);
        }
        if (StringUtils.Equals(rawSource, "Refiner"))
        {
            return (ImageReferenceSourceKind.Refiner, null);
        }
        if (ImageReference.TryParseBase2EditStageIndex(rawSource, out int editStage))
        {
            return (ImageReferenceSourceKind.Base2Edit, editStage);
        }
        return (ImageReferenceSourceKind.Unknown, null);
    }

    private static StageAudioAction CompileAudioAction(AudioPlan audio, StageSpec stage)
    {
        if (!audio.Reuse.IsEligible)
        {
            return StageAudioAction.None;
        }
        if (stage.ClipStageIndex == audio.Reuse.CaptureStageIndex)
        {
            return StageAudioAction.CaptureForReuse;
        }
        return stage.ClipStageIndex >= audio.Reuse.ReuseFromStageIndex
            ? StageAudioAction.ReuseCaptured
            : StageAudioAction.None;
    }

    private static List<BoundaryPlan> BuildBoundaries(
        IReadOnlyList<ClipSpec> clips,
        List<VideoPlanDiagnostic> diagnostics)
    {
        List<BoundaryPlan> boundaries = [];
        for (int i = 0; i < clips.Count - 1; i++)
        {
            ClipSpec from = clips[i];
            BoundaryExecutionMode requested = ParseBoundaryMode(from.BoundaryOut, out bool isKnown);
            ClipSpec to = clips[i + 1];
            BoundaryFallback fallback = isKnown ? BoundaryFallback.None : BoundaryFallback.UnknownBoundaryKind;
            BoundaryExecutionMode effective = requested;
            if (!isKnown)
            {
                effective = BoundaryExecutionMode.Cut;
            }
            else if (requested == BoundaryExecutionMode.Continue && to.SourceVideo is not null)
            {
                effective = BoundaryExecutionMode.Cut;
                fallback = BoundaryFallback.TargetIsSourcedVideo;
            }
            else if (requested == BoundaryExecutionMode.Continue && to.Stages is not { Count: > 0 })
            {
                effective = BoundaryExecutionMode.Cut;
                fallback = BoundaryFallback.TargetHasNoStage;
            }
            else if (requested == BoundaryExecutionMode.Continue && HasExplicitFirstFrameReference(to))
            {
                effective = BoundaryExecutionMode.Cut;
                fallback = BoundaryFallback.TargetHasFirstFrameReference;
            }

            int overlap = effective == BoundaryExecutionMode.Cut ? 0 : NormalizeOverlap(from.BoundaryOutOverlap);
            int continuityWindow = effective == BoundaryExecutionMode.Continue ? overlap + 1 : 0;
            if (fallback != BoundaryFallback.None)
            {
                diagnostics.Add(new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Warning,
                    $"boundary-{fallback.ToString().ToLowerInvariant()}",
                    $"Clip {from.Id} boundary '{from.BoundaryOut}' falls back to a cut: {DescribeFallback(fallback)}",
                    from.Id));
            }
            boundaries.Add(new BoundaryPlan(
                from.Id,
                to.Id,
                requested,
                effective,
                overlap,
                continuityWindow,
                RequiresRuntimeMergeValidation: effective != BoundaryExecutionMode.Cut,
                fallback));
        }
        return boundaries;
    }

    private static BoundaryExecutionMode ParseBoundaryMode(string value, out bool isKnown)
    {
        if (string.Equals(value, Constants.BoundaryOutContinue, StringComparison.OrdinalIgnoreCase))
        {
            isKnown = true;
            return BoundaryExecutionMode.Continue;
        }
        if (string.Equals(value, Constants.BoundaryOutCrossfade, StringComparison.OrdinalIgnoreCase))
        {
            isKnown = true;
            return BoundaryExecutionMode.Crossfade;
        }
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, Constants.BoundaryOutCut, StringComparison.OrdinalIgnoreCase))
        {
            isKnown = true;
            return BoundaryExecutionMode.Cut;
        }
        isKnown = false;
        return BoundaryExecutionMode.Cut;
    }

    private static int NormalizeOverlap(int overlap) => Math.Clamp(
        overlap < Constants.ContinueOverlapDefaultFrames
            ? Constants.ContinueOverlapDefaultFrames
            : overlap - (overlap % 8),
        Constants.ContinueOverlapDefaultFrames,
        Constants.ContinueOverlapMaxFrames);

    private static bool HasExplicitFirstFrameReference(ClipSpec clip) =>
        clip.ImageRefs?.Any(reference => !reference.FromEnd && reference.Frame == 1) == true;

    private static string DescribeFallback(BoundaryFallback fallback) => fallback switch
    {
        BoundaryFallback.TargetIsSourcedVideo => "the next clip is sourced footage",
        BoundaryFallback.TargetHasNoStage => "the next clip has no stage that can consume continuity",
        BoundaryFallback.TargetHasFirstFrameReference => "the next clip has an explicit first-frame reference",
        BoundaryFallback.UnknownBoundaryKind => "the requested boundary mode is unknown",
        _ => "the boundary is not applicable",
    };

}
