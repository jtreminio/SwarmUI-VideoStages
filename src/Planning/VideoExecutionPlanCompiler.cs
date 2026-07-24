using System.Collections.Immutable;

namespace VideoStages.Planning;

using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;

/// <summary>
/// Compiles the parsed VideoStages specification into a deterministic architecture execution plan. This is
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
        => Compile(
            spec,
            rootEnvironment,
            ArchitecturePlanResolver.Resolve(spec, VideoArchitectureRegistry.Production));

    internal static VideoExecutionPlan Compile(
        VideoStagesSpec spec,
        RootEnvironment rootEnvironment,
        ArchitecturePlanningResult architecturePlanning)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(rootEnvironment);
        ArgumentNullException.ThrowIfNull(architecturePlanning);

        List<VideoPlanDiagnostic> diagnostics = [.. architecturePlanning.Diagnostics];
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

        RootPlan root = RootPlanCompiler.Compile(rootEnvironment, activeClips);
        List<ClipPlan> clips = [];
        int totalStageCount = activeClips.Sum(clip => clip.Stages?.Count ?? 0);
        int firstStageOrdinal = 0;
        for (int i = 0; i < activeClips.Count; i++)
        {
            ClipArchitectureAssignment assignment =
                architecturePlanning.Clips.GetValueOrDefault(activeClips[i].Id);
            IArchitectureClipPayload architecturePayload = null;
            if (assignment is not null)
            {
                IReadOnlyList<VideoPlanDiagnostic> capabilityDiagnostics =
                    ArchitectureCapabilityValidator.Validate(
                        activeClips[i],
                        assignment.Architecture,
                        ResolveEntryMode(spec, rootEnvironment, activeClips[i]),
                        assignment.StageModels);
                diagnostics.AddRange(capabilityDiagnostics);
                if (!capabilityDiagnostics.Any(diagnostic =>
                    diagnostic.Severity == VideoPlanDiagnosticSeverity.Error))
                {
                    ArchitectureEntryMode entryMode = ResolveEntryMode(
                        spec,
                        rootEnvironment,
                        activeClips[i]);
                    ArchitectureClipCompilation architectureCompilation =
                        assignment.Module.ValidateAndCompileClip(
                            activeClips[i],
                            assignment.StageModels,
                            new(
                                spec.Width,
                                spec.Height,
                                spec.FPS,
                                entryMode,
                                HasPreviousClipOutput: i > 0));
                    diagnostics.AddRange(architectureCompilation.Diagnostics);
                    if (!architectureCompilation.Diagnostics.Any(diagnostic =>
                        diagnostic.Severity == VideoPlanDiagnosticSeverity.Error))
                    {
                        architecturePayload = architectureCompilation.Payload;
                    }
                }
            }
            clips.Add(ClipPlanCompiler.Compile(
                activeClips[i],
                new ClipPlanCompilationContext(
                    spec.IsTextToVideo,
                    spec.Width,
                    spec.Height,
                    spec.FPS,
                    i == activeClips.Count - 1,
                    activeClips.Count > 1,
                    totalStageCount,
                    firstStageOrdinal,
                    assignment,
                    architecturePayload)));
            firstStageOrdinal += activeClips[i].Stages?.Count ?? 0;
        }
        BoundaryPlanningResult boundaryResult = BoundaryPlanCompiler.Compile(activeClips, clips);
        diagnostics.AddRange(boundaryResult.Diagnostics);
        BoundaryBudgetResolution boundaryBudget = BoundaryOverlapPlanner.ResolvePlanBudgets(
            [.. activeClips.Select(clip => clip.Frames)],
            boundaryResult.Boundaries);
        IReadOnlyList<BoundaryPlan> resolvedBoundaries = boundaryBudget.Boundaries;
        if (boundaryBudget.Degraded)
        {
            diagnostics.Add(new VideoPlanDiagnostic(
                VideoPlanDiagnosticSeverity.Warning,
                "boundary-frame-budget-reconciled",
                $"VideoStages: {boundaryBudget.Reason}."));
        }
        IVideoArchitectureModule[] modules = [
            .. activeClips
                .Select(clip => architecturePlanning.Clips.GetValueOrDefault(clip.Id)?.Module)
                .Where(module => module is not null)
                .Distinct()
        ];
        foreach (IArchitecturePlanValidator validator in modules.OfType<IArchitecturePlanValidator>())
        {
            HashSet<int> architectureClipIds = [
                .. activeClips
                    .Where(clip => ReferenceEquals(
                        architecturePlanning.Clips.GetValueOrDefault(clip.Id)?.Module,
                        validator))
                    .Select(clip => clip.Id)
            ];
            diagnostics.AddRange(validator.ValidatePlan(
                [.. clips.Where(clip => architectureClipIds.Contains(clip.ClipId))],
                clips,
                root));
        }

        VideoExecutionPlan plan = new(
            spec.Width,
            spec.Height,
            spec.FPS,
            root,
            Array.AsReadOnly(clips.ToArray()),
            Array.AsReadOnly(resolvedBoundaries.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()));
        ImmutableArray<AudioTrackSpec> authoredAudioTracks =
            TimelineAudioSegmentTrackSpecPlanner.Compile(
                spec.TimelineAudioSegments,
                plan);
        AudioTimelinePlan audioTimeline = AudioTimelinePlanCompiler.Compile(
            plan,
            authoredAudioTracks);
        IReadOnlyList<ClipPlan> clipsWithTimelineAudio =
            TimelineAudioSegmentPlanProjector.Apply(
                plan.Clips,
                audioTimeline,
                authoredAudioTracks.Select(track => track.TrackId).ToHashSet(
                    StringComparer.Ordinal));
        // Audio diagnostics are collected only once the timeline projection has run, because the
        // projected segments are what a clip's audio plan finally owns.
        foreach (ClipPlan clipPlan in clipsWithTimelineAudio)
        {
            diagnostics.AddRange(clipPlan.Audio.Diagnostics.Select(audioDiagnostic =>
                new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Warning,
                    audioDiagnostic.Code,
                    audioDiagnostic.Message,
                    clipPlan.ClipId)));
            VideoArchitectureDescriptor descriptor = architecturePlanning.Clips
                .GetValueOrDefault(clipPlan.ClipId)?.Architecture;
            if (descriptor is not null)
            {
                diagnostics.AddRange(
                    ArchitectureCapabilityValidator.ValidateProjectedAudioSegments(
                        clipPlan,
                        descriptor));
            }
        }
        diagnostics.AddRange(audioTimeline.Diagnostics.Select(MapAudioTimelineDiagnostic));
        return plan with
        {
            HasConfiguredResolution = spec.HasConfiguredResolution,
            Clips = clipsWithTimelineAudio,
            Diagnostics = Array.AsReadOnly(diagnostics.ToArray()),
            AudioTimeline = audioTimeline,
        };
    }

    private static VideoPlanDiagnostic MapAudioTimelineDiagnostic(
        AudioTimelineDiagnostic diagnostic) =>
        new(
            diagnostic.Severity switch
            {
                AudioTimelineDiagnosticSeverity.Info => VideoPlanDiagnosticSeverity.Info,
                AudioTimelineDiagnosticSeverity.Warning => VideoPlanDiagnosticSeverity.Warning,
                _ => VideoPlanDiagnosticSeverity.Error,
            },
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.ClipId);

    private static bool IsExecutableClip(ClipSpec clip) =>
        clip is not null && (clip.SourceVideo is not null || clip.Stages is { Count: > 0 });

    private static ArchitectureEntryMode ResolveEntryMode(
        VideoStagesSpec spec,
        RootEnvironment rootEnvironment,
        ClipSpec clip) =>
        clip.SourceVideo is not null
            ? ArchitectureEntryMode.SourceVideo
            : rootEnvironment.HostKind == HostRootKind.GlobalRefineSource
                ? ArchitectureEntryMode.RefineVideo
                : spec.IsTextToVideo
                    ? ArchitectureEntryMode.TextToVideo
                    : ArchitectureEntryMode.ImageToVideo;

}
