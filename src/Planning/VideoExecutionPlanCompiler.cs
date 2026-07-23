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

        RootPlan root = RootPlanCompiler.Compile(rootEnvironment, activeClips);
        BoundaryPlanningResult boundaryResult = BoundaryPlanCompiler.Compile(activeClips);
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
        List<ClipPlan> clips = [];
        int totalStageCount = activeClips.Sum(clip => clip.Stages?.Count ?? 0);
        int firstStageOrdinal = 0;
        for (int i = 0; i < activeClips.Count; i++)
        {
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
                    firstStageOrdinal)));
            diagnostics.AddRange(clips[^1].Audio.Diagnostics.Select(audioDiagnostic =>
                new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Warning,
                    audioDiagnostic.Code,
                    audioDiagnostic.Message,
                    clips[^1].ClipId)));
            firstStageOrdinal += activeClips[i].Stages?.Count ?? 0;
        }
        diagnostics.AddRange(VideoPlanValidationCompiler.Validate(clips));

        VideoExecutionPlan plan = new(
            spec.Width,
            spec.Height,
            spec.FPS,
            root,
            Array.AsReadOnly(clips.ToArray()),
            Array.AsReadOnly(resolvedBoundaries.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()));
        AudioTimelinePlan audioTimeline = AudioTimelinePlanCompiler.Compile(plan);
        diagnostics.AddRange(audioTimeline.Diagnostics.Select(MapAudioTimelineDiagnostic));
        return plan with
        {
            HasConfiguredResolution = spec.HasConfiguredResolution,
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

}
