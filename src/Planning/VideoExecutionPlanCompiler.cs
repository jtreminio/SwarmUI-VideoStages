using System.Collections.Immutable;

namespace VideoStages.Planning;

using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;

/// <summary>
/// Compiles a spec into a deterministic plan without inspecting or mutating the host workflow.
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
            EffectiveVideoRequestProjection.Project(
                spec,
                rootEnvironment.HostKind,
                architecturePlanning);
        TimelineSpec effective = request.Spec;
        List<PlanDiagnostic> diagnostics =
        [
            .. architecturePlanning.Diagnostics,
            .. request.Diagnostics,
        ];
        if (effective.LegacyVideoSwap?.IsConfigured == true
            && architecturePlanning.Clips.Values.Any(assignment =>
                assignment?.Architecture.RunsOnStockHostSampler == true))
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.video-swap-ignored",
                "VideoStages ignores SwarmUI's request-global Video Swap Model, Video Swap "
                    + "Percent, and Video Swap section settings for stock-host video clips. "
                    + "The authored values remain in request metadata. Create separate timeline "
                    + "stages instead."));
        }
        IReadOnlyList<ClipSpec> activeClips = (effective.Clips ?? []).Where(IsExecutableClip).ToArray();
        if (activeClips.Count != (effective.Clips?.Count ?? 0))
        {
            diagnostics.Add(new PlanDiagnostic(
                PlanDiagnosticSeverity.Warning,
                "inactive-clips-ignored",
                "Clips without a source video or active stages were ignored by the execution plan."));
        }

        RootPlan root = RootPlanCompiler.Compile(rootEnvironment, activeClips);
        List<ClipPlan> clips = [];
        int totalStageCount = activeClips.Sum(clip => clip.Stages?.Count ?? 0);
        int firstStageOrdinal = 0;
        (int Width, int Height) previousOutputDimensions =
            (effective.Width, effective.Height);
        for (int i = 0; i < activeClips.Count; i++)
        {
            ClipSpec activeClip = activeClips[i];
            (int Width, int Height) inputDimensions =
                i > 0
                && StringUtils.Equals(
                    activeClip.InitVideo?.Source,
                    MediaSource.PreviousClip)
                    ? previousOutputDimensions
                    : (effective.Width, effective.Height);
            ClipArchitectureAssignment assignment =
                architecturePlanning.Clips.GetValueOrDefault(activeClip.Id);
            ArchitectureEntryMode entryMode = ResolveEntryMode(effective, activeClip);
            ArchitectureClipCompilation acceptedArchitectureCompilation = null;
            if (assignment is not null
                && !architecturePlanning.IsBlocked(activeClip.Id))
            {
                IReadOnlyList<PlanDiagnostic> capabilityDiagnostics =
                    ArchitectureCapabilityValidator.Validate(
                        activeClip,
                        assignment.Architecture,
                        entryMode,
                        hasOutgoingBoundary: i < activeClips.Count - 1);
                diagnostics.AddRange(capabilityDiagnostics);
                if (!capabilityDiagnostics.Any(diagnostic =>
                    diagnostic.Severity == PlanDiagnosticSeverity.Error))
                {
                    ArchitectureClipCompilation architectureCompilation =
                        CompileArchitecture(
                            activeClip,
                            assignment,
                            new(
                                inputDimensions.Width,
                                inputDimensions.Height,
                                effective.FPS,
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
            ClipPlan clipPlan = ClipPlanCompiler.Compile(
                activeClip,
                new ClipPlanCompilationContext(
                    inputDimensions.Width,
                    inputDimensions.Height,
                    effective.FPS,
                    totalStageCount,
                    firstStageOrdinal,
                    entryMode,
                    assignment,
                    acceptedArchitectureCompilation));
            clips.Add(clipPlan);
            previousOutputDimensions = acceptedArchitectureCompilation?.Payload
                .ProjectFinalDimensions(
                    clipPlan.Stages,
                    inputDimensions.Width,
                    inputDimensions.Height)
                ?? inputDimensions;
            firstStageOrdinal += activeClip.Stages?.Count ?? 0;
        }
        diagnostics.AddRange(ClipGeometryValidator.Validate(clips, effective.Width, effective.Height));
        BoundaryPlanningResult boundaryResult = BoundaryPlanCompiler.Compile(
            activeClips,
            clips);
        diagnostics.AddRange(boundaryResult.Diagnostics);
        BoundaryBudgetResolution boundaryBudget = BoundaryOverlaps.FitPlanToFrameBudgets(
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
        TimelineAudioSpanCompilation audio = TimelineAudioSpanCompiler.Compile(
            effective.FPS,
            clips,
            resolvedBoundaries,
            effective.TimelineAudioSpans);
        IReadOnlyList<ClipPlan> clipsWithTimelineAudio = audio.Clips;
        foreach (ClipPlan clipPlan in clipsWithTimelineAudio)
        {
            diagnostics.AddRange(clipPlan.Audio.Diagnostics.Select(audioDiagnostic =>
                audioDiagnostic with { ClipId = audioDiagnostic.ClipId ?? clipPlan.ClipId }));
        }
        diagnostics.AddRange(audio.Diagnostics);
        return new(
            effective.Width,
            effective.Height,
            effective.FPS,
            root,
            clipsWithTimelineAudio,
            Array.AsReadOnly(resolvedBoundaries.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()))
        {
            HasConfiguredResolution = effective.HasConfiguredResolution,
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
            return NoneArchitecture.EmptyCompilation;
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
