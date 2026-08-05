using System.Collections.Immutable;

namespace VideoStages.Planning;

using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.Wan;

/// <summary>
/// Compiles a parsed specification into a deterministic plan without inspecting or mutating the
/// host workflow. Architecture assignments must be resolved for the request before compilation.
/// </summary>
internal static class VideoExecutionPlanCompiler
{
    internal static VideoExecutionPlan Compile(
        TimelineSpec spec,
        RootEnvironment rootEnvironment,
        ArchitecturePlanningResult architecturePlanning)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(rootEnvironment);
        ArgumentNullException.ThrowIfNull(architecturePlanning);

        EffectiveVideoRequest request =
            EffectiveVideoRequestProjector.Project(
                spec,
                rootEnvironment,
                architecturePlanning);
        spec = request.Spec;
        List<PlanDiagnostic> diagnostics =
        [
            .. architecturePlanning.Diagnostics,
            .. request.Diagnostics,
        ];
        if (spec.LegacyVideoSwap?.IsConfigured == true
            && architecturePlanning.Clips.Values.Any(assignment =>
                assignment?.Architecture.Id == HostVideoArchitectureModule.ArchitectureId
                || assignment?.Architecture.Id == WanArchitectureModule.ArchitectureId))
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.video-swap-ignored",
                "VideoStages ignores SwarmUI's request-global Video Swap Model, Video Swap "
                    + "Percent, and Video Swap section settings for stock-host video clips. "
                    + "The authored values remain in request metadata. Create separate timeline "
                    + "stages instead."));
        }
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
                    PlanDiagnosticSeverity.Warning,
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
            bool architectureResolutionBlocked =
                architecturePlanning.Diagnostics.Any(diagnostic =>
                    diagnostic.Severity == PlanDiagnosticSeverity.Error
                    && diagnostic.ClipId == activeClips[i].Id);
            ClipArchitectureAssignment assignment =
                architecturePlanning.Clips.GetValueOrDefault(activeClips[i].Id);
            ArchitectureEntryMode entryMode = ResolveEntryMode(spec, activeClips[i]);
            ArchitectureClipCompilation acceptedArchitectureCompilation = null;
            if (assignment is not null
                && !architectureResolutionBlocked)
            {
                IReadOnlyList<PlanDiagnostic> capabilityDiagnostics =
                    ArchitectureCapabilityValidator.Validate(
                        activeClips[i],
                        assignment.Architecture,
                        entryMode,
                        hasOutgoingBoundary: i < activeClips.Count - 1);
                diagnostics.AddRange(capabilityDiagnostics);
                if (!capabilityDiagnostics.Any(diagnostic =>
                    diagnostic.Severity == PlanDiagnosticSeverity.Error))
                {
                    ArchitectureClipCompilation architectureCompilation =
                        CompileArchitecture(
                            activeClips[i],
                            assignment,
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
            clips);
        diagnostics.AddRange(boundaryResult.Diagnostics);
        BoundaryBudgetResolution boundaryBudget = BoundaryOverlapPlanner.FitPlanToFrameBudgets(
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
        // Timeline audio remains architecture-neutral; non-consuming architectures overlay it after decode.
        TimelineAudioSegmentCompilation audio = TimelineAudioSegmentPlanCompiler.Compile(
            spec.FPS,
            clips,
            resolvedBoundaries,
            spec.TimelineAudioSegments);
        IReadOnlyList<ClipPlan> clipsWithTimelineAudio = audio.Clips;
        foreach (ClipPlan clipPlan in clipsWithTimelineAudio)
        {
            diagnostics.AddRange(clipPlan.Audio.Diagnostics.Select(audioDiagnostic =>
                audioDiagnostic with { ClipId = audioDiagnostic.ClipId ?? clipPlan.ClipId }));
        }
        diagnostics.AddRange(audio.Diagnostics);
        return new(
            spec.Width,
            spec.Height,
            spec.FPS,
            root,
            clipsWithTimelineAudio,
            Array.AsReadOnly(resolvedBoundaries.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()))
        {
            HasConfiguredResolution = spec.HasConfiguredResolution,
        };
    }

    private static bool IsExecutableClip(ClipSpec clip) =>
        clip is not null && (clip.InitVideo is not null || clip.Stages is { Count: > 0 });

    private static ArchitectureClipCompilation CompileArchitecture(
        ClipSpec clip,
        ClipArchitectureAssignment assignment,
        ArchitectureClipCompileContext context)
    {
        if (assignment.Module is not null)
        {
            return assignment.Module.ValidateAndCompileClip(
                clip,
                assignment.StageModels,
                context);
        }
        if (assignment.Architecture.Id == NoneArchitecture.Id)
        {
            return new(
                new NoneClipPayload(),
                new Dictionary<int, IArchitectureStagePayload>(),
                []);
        }
        throw Invariant.Failure(
            $"architecture '{assignment.Architecture.Id}' has no clip compiler");
    }

    private static ArchitectureEntryMode ResolveEntryMode(
        TimelineSpec spec,
        ClipSpec clip) =>
        clip.InitVideo is not null
            ? ArchitectureEntryMode.InitVideo
            : spec.IsTextToVideo
                ? ArchitectureEntryMode.TextToVideo
                : ArchitectureEntryMode.ImageToVideo;

}
