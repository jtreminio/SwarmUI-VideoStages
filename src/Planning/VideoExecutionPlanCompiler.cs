using System.Collections.Immutable;

namespace VideoStages.Planning;

using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;

/// <summary>
/// Compiles the parsed VideoStages specification into a deterministic architecture execution plan. This is
/// a pure transformation: it neither inspects nor mutates the host workflow or its graph.
/// <para>
/// Architecture resolution is a required argument, never a default: resolving it here would mean
/// resolving it against the unfiltered production registry, with no request session to authorize
/// the models the plan names.
/// </para>
/// </summary>
internal static class VideoExecutionPlanCompiler
{
    internal static VideoExecutionPlan Compile(
        VideoStagesSpec spec,
        RootEnvironment rootEnvironment,
        ArchitecturePlanningResult architecturePlanning)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(rootEnvironment);
        ArgumentNullException.ThrowIfNull(architecturePlanning);

        EffectiveVideoRequest request =
            EffectiveVideoRequestProjector.Project(spec, architecturePlanning);
        spec = request.Spec;
        architecturePlanning = request.ArchitecturePlanning;
        List<PlanDiagnostic> diagnostics =
        [
            .. architecturePlanning.Diagnostics,
            .. request.Diagnostics,
        ];
        IReadOnlyList<ClipSpec> executableClips = (spec.Clips ?? []).Where(IsExecutableClip).ToArray();
        if (executableClips.Count != (spec.Clips?.Count ?? 0))
        {
            diagnostics.Add(new PlanDiagnostic(
                PlanDiagnosticSeverity.Warning,
                "inactive-clips-ignored",
                "Clips without a source video or active stages were ignored by the execution plan."));
        }
        List<ClipSpec> activeClips = [];
        HashSet<int> seenClipIds = [];
        foreach (ClipSpec clip in executableClips)
        {
            if (!seenClipIds.Add(clip.Id))
            {
                diagnostics.Add(new PlanDiagnostic(
                    PlanDiagnosticSeverity.Error,
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
            bool effectiveRequestBlocked =
                request.BlockedClipIds.Contains(activeClips[i].Id);
            bool architectureResolutionBlocked =
                architecturePlanning.Diagnostics.Any(diagnostic =>
                    diagnostic.Severity == PlanDiagnosticSeverity.Error
                    && diagnostic.ClipId == activeClips[i].Id);
            ClipArchitectureAssignment assignment =
                architecturePlanning.Clips.GetValueOrDefault(activeClips[i].Id);
            ArchitectureEntryMode entryMode = ResolveEntryMode(spec, rootEnvironment, activeClips[i]);
            ArchitectureClipCompilation acceptedArchitectureCompilation = null;
            if (assignment is not null
                && !effectiveRequestBlocked
                && !architectureResolutionBlocked)
            {
                IReadOnlyList<PlanDiagnostic> capabilityDiagnostics =
                    ArchitectureCapabilityValidator.Validate(
                        activeClips[i],
                        assignment.Architecture,
                        entryMode,
                        assignment.StageModels);
                diagnostics.AddRange(capabilityDiagnostics);
                if (!capabilityDiagnostics.Any(diagnostic =>
                    diagnostic.Severity == PlanDiagnosticSeverity.Error))
                {
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
                        diagnostic.Severity == PlanDiagnosticSeverity.Error))
                    {
                        acceptedArchitectureCompilation = architectureCompilation;
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
                    entryMode,
                    assignment,
                    acceptedArchitectureCompilation)));
            firstStageOrdinal += activeClips[i].Stages?.Count ?? 0;
        }
        diagnostics.AddRange(ClipGeometryProjection.Validate(clips, spec.Width, spec.Height));
        BoundaryPlanningResult boundaryResult = BoundaryPlanCompiler.Compile(
            activeClips,
            clips,
            request.AuthoredBoundaryModes,
            request.ProjectedBoundaryFallbacks);
        diagnostics.AddRange(boundaryResult.Diagnostics);
        BoundaryBudgetResolution boundaryBudget = BoundaryOverlapPlanner.ResolvePlanBudgets(
            [.. activeClips.Select(clip => clip.Frames)],
            boundaryResult.Boundaries);
        IReadOnlyList<BoundaryPlan> resolvedBoundaries = boundaryBudget.Boundaries;
        if (boundaryBudget.Degraded)
        {
            diagnostics.Add(new PlanDiagnostic(
                PlanDiagnosticSeverity.Warning,
                "boundary-frame-budget-reconciled",
                $"{boundaryBudget.Reason}."));
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
        HashSet<int> audioSegmentUnsupportedClipIds = [
            .. plan.Clips
                .Where(clip =>
                    clip.Architecture is not null
                    && !clip.Architecture.Capabilities.Clip.HasFlag(
                        ClipCapability.AudioSegments))
                .Select(clip => clip.ClipId),
        ];
        bool noClipSupportsAudioSegments =
            plan.Clips.Count > 0
            && audioSegmentUnsupportedClipIds.Count == plan.Clips.Count;
        bool authoredTimelineAudio =
            spec.TimelineAudioSegments is { Count: > 0 };
        AudioTimelineCompilation audio = AudioTimelinePlanCompiler.Compile(
            plan,
            noClipSupportsAudioSegments ? null : spec.TimelineAudioSegments);
        AudioTimelinePlan audioTimeline = audio.Plan;
        HashSet<string> authoredTrackIds = audio.AuthoredTracks
            .Select(track => track.TrackId)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<int> suppressedTimelineAudioClipIds = [
            .. audioTimeline.Tracks
                .Where(track => authoredTrackIds.Contains(track.TrackId))
                .SelectMany(track => track.Windows)
                .Select(window => window.ClipId)
                .Where(audioSegmentUnsupportedClipIds.Contains),
        ];
        if (noClipSupportsAudioSegments && authoredTimelineAudio)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.audio-segments-ignored",
                "None of the selected video architectures use timeline audio tracks. The "
                    + "authored tracks remain saved and are ignored for this generation."));
        }
        else
        {
            foreach (int clipId in suppressedTimelineAudioClipIds)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "effective-request.audio-segments-ignored",
                    $"Timeline audio overlaps clip {clipId}, whose selected video architecture "
                        + "does not use audio segments. The authored track remains saved and is "
                        + "ignored for that clip.",
                    clipId));
            }
        }
        IReadOnlyList<ClipPlan> clipsWithTimelineAudio =
            TimelineAudioSegmentPlanProjector.Apply(
                plan.Clips,
                audioTimeline,
                authoredTrackIds,
                suppressedTimelineAudioClipIds);
        // Audio diagnostics are collected only once the timeline projection has run, because the
        // projected segments are what a clip's audio plan finally owns.
        foreach (ClipPlan clipPlan in clipsWithTimelineAudio)
        {
            diagnostics.AddRange(clipPlan.Audio.Diagnostics.Select(audioDiagnostic =>
                audioDiagnostic with { ClipId = audioDiagnostic.ClipId ?? clipPlan.ClipId }));
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
        diagnostics.AddRange(audioTimeline.Diagnostics);
        return plan with
        {
            HasConfiguredResolution = spec.HasConfiguredResolution,
            Clips = clipsWithTimelineAudio,
            Diagnostics = Array.AsReadOnly(diagnostics.ToArray()),
            AudioTimeline = audioTimeline,
        };
    }

    private static bool IsExecutableClip(ClipSpec clip) =>
        clip is not null && (clip.SourceVideo is not null || clip.Stages is { Count: > 0 });

    private static ArchitectureEntryMode ResolveEntryMode(
        VideoStagesSpec spec,
        RootEnvironment rootEnvironment,
        ClipSpec clip) =>
        clip.SourceVideo is not null
            ? ArchitectureEntryMode.SourceVideo
            // The environment always reports the host kind; global refine arrives as its own flag
            // and only RootPlanCompiler promotes it to a root kind.
            : rootEnvironment.HasGlobalRefineSource
                ? ArchitectureEntryMode.RefineVideo
                : spec.IsTextToVideo
                    ? ArchitectureEntryMode.TextToVideo
                    : ArchitectureEntryMode.ImageToVideo;

}
