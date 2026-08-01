using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.Planning;

/// <summary>Post-resolution request values and projection diagnostics.</summary>
internal sealed record EffectiveVideoRequest(
    VideoStagesSpec Spec,
    IReadOnlyList<PlanDiagnostic> Diagnostics);

/// <summary>
/// Projects resolved temporal grids and reports stale model hints.
/// </summary>
internal static class EffectiveVideoRequestProjector
{
    private sealed class EffectiveClipPlanningContext(
        int timelineIndex,
        ClipSpec authored,
        ClipArchitectureAssignment assignment)
    {
        internal int TimelineIndex { get; } = timelineIndex;
        internal ClipSpec Authored { get; } = authored;
        internal ClipArchitectureAssignment Assignment { get; } = assignment;
        internal ClipSpec Effective { get; set; } = authored;
        internal List<PlanDiagnostic> Diagnostics { get; } = [];
    }

    internal static EffectiveVideoRequest Project(
        VideoStagesSpec authored,
        ArchitecturePlanningResult architecturePlanning) =>
        Project(authored, RootEnvironment.FromSpec(authored), architecturePlanning);

    internal static EffectiveVideoRequest Project(
        VideoStagesSpec authored,
        RootEnvironment rootEnvironment,
        ArchitecturePlanningResult architecturePlanning)
    {
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(rootEnvironment);
        ArgumentNullException.ThrowIfNull(architecturePlanning);

        EffectiveClipPlanningContext[] clips = (authored.Clips ?? [])
            .Select((clip, timelineIndex) => new EffectiveClipPlanningContext(
                timelineIndex,
                clip,
                architecturePlanning.Clips.GetValueOrDefault(clip.Id)))
            .ToArray();
        foreach (EffectiveClipPlanningContext clip in clips)
        {
            if (clip.Assignment is null)
            {
                continue;
            }
            WarnAboutResolvedIdentityHints(
                clip.Authored,
                clip.Assignment,
                clip.Diagnostics);
        }

        int rootTimelineIndex = Array.FindIndex(
            clips,
            clip => clip.Authored.InitVideo is null
                && clip.Authored.Stages is { Count: > 0 });
        bool rootCanForceTextToVideoGeneration =
            rootEnvironment.HostKind == HostRootKind.TextToVideoRoot;
        foreach (EffectiveClipPlanningContext clip in clips)
        {
            bool architectureResolutionBlocked = architecturePlanning.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == PlanDiagnosticSeverity.Error
                && diagnostic.ClipId == clip.Effective.Id);
            if (clip.Assignment is null || architectureResolutionBlocked)
            {
                continue;
            }
            clip.Effective = ProjectResolvedTemporalGrid(
                clip.Authored,
                clip.Effective,
                clip.Assignment,
                rootCanForceTextToVideoGeneration
                    && clip.TimelineIndex == rootTimelineIndex,
                clip.Diagnostics);
        }

        return new(
            authored with
            {
                Clips = Array.AsReadOnly(clips.Select(clip => clip.Effective).ToArray()),
            },
            Array.AsReadOnly(clips.SelectMany(clip => clip.Diagnostics).ToArray()));
    }

    private static void WarnAboutResolvedIdentityHints(
        ClipSpec authored,
        ClipArchitectureAssignment assignment,
        ICollection<PlanDiagnostic> diagnostics)
    {
        int? firstRawIndex = authored.Stages?.FirstOrDefault()?.ClipStageRawIndex;
        if (!firstRawIndex.HasValue
            && assignment.Architecture.Id != NoneArchitecture.Id)
        {
            firstRawIndex = authored.AuthoredStages?.FirstOrDefault()?.RawIndex;
        }
        if (firstRawIndex.HasValue
            && assignment.StageModels.TryGetValue(
                firstRawIndex.Value,
                out ResolvedVideoModel firstModel)
            && firstModel is not null)
        {
            if (!string.IsNullOrWhiteSpace(authored.AuthoredArchitectureHint)
                && !string.Equals(
                    authored.AuthoredArchitectureHint,
                    firstModel.ArchitectureId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "effective-request.stale-architecture-hint",
                    $"Clip {authored.Id} cached architecture hint '{authored.AuthoredArchitectureHint}' "
                        + $"does not match resolved model '{firstModel.ModelName}'. Generation uses "
                        + $"'{firstModel.ArchitectureId}' and preserves the authored hint.",
                    authored.Id,
                    RawStageIndex: firstRawIndex));
            }
            if (!string.IsNullOrWhiteSpace(authored.AuthoredModelProfileHint)
                && !string.Equals(
                    authored.AuthoredModelProfileHint,
                    firstModel.ModelProfileId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "effective-request.stale-clip-profile-hint",
                    $"Clip {authored.Id} cached model profile '{authored.AuthoredModelProfileHint}' "
                        + $"does not match resolved model '{firstModel.ModelName}'. Generation uses "
                        + $"'{firstModel.ModelProfileId}' and preserves the authored hint.",
                    authored.Id,
                    RawStageIndex: firstRawIndex));
            }
        }

        foreach (AuthoredStageModelSpec stage in authored.AuthoredStages ?? [])
        {
            if (!assignment.StageModels.TryGetValue(
                    stage.RawIndex,
                    out ResolvedVideoModel resolved)
                || resolved is null
                || string.IsNullOrWhiteSpace(stage.ModelProfileId)
                || string.Equals(
                    stage.ModelProfileId,
                    resolved.ModelProfileId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.stale-stage-profile-hint",
                $"Clip {authored.Id} authored stage {stage.RawIndex} cached model profile "
                    + $"'{stage.ModelProfileId}', but model '{resolved.ModelName}' resolves "
                    + $"to '{resolved.ModelProfileId}'. Generation uses the resolved profile "
                    + "and preserves the authored hint.",
                authored.Id,
                RawStageIndex: stage.RawIndex));
        }
    }

    private static ClipSpec ProjectResolvedTemporalGrid(
        ClipSpec authoredCanonical,
        ClipSpec projected,
        ClipArchitectureAssignment assignment,
        bool forceRootStageGeneration,
        ICollection<PlanDiagnostic> diagnostics)
    {
        // Only an architecture that declares the feature derives the length at runtime, and only
        // from a source that can supply one; otherwise the authored flag is inert and must not
        // suppress grid snapping.
        ArchitectureFeature features = assignment.Architecture.Features;
        if (!projected.Frames.HasValue
            || projected.Stages is not { Count: > 0 }
            || (AudioSourceKindPolicy.AudioOwnsClipDuration(projected)
                && features.HasFlag(ArchitectureFeature.AudioDerivedDuration))
            || (projected.ClipLengthFromControlNet
                && features.HasFlag(ArchitectureFeature.IcLora)))
        {
            return projected;
        }

        List<int> activeGrids = [];
        foreach (StageSpec stage in projected.Stages.Where(stage =>
            !ArchitectureStageActivity.IsPassthrough(
                stage,
                assignment.Architecture)
            || (forceRootStageGeneration && stage.ClipStageIndex == 0)))
        {
            if (!assignment.StageModels.TryGetValue(
                    stage.ClipStageRawIndex,
                    out ResolvedVideoModel resolved))
            {
                // Architecture planning owns the unresolved-stage diagnostic; leave the grid unresolved.
                return projected;
            }
            activeGrids.Add(resolved.FrameGrid);
        }
        if (activeGrids.Count == 0)
        {
            return projected;
        }

        int frameGrid;
        try
        {
            frameGrid = StaticGeneratedFrameGrid.CompatibleGrid(activeGrids);
        }
        catch (OverflowException)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.temporal-grid-conflict",
                $"Clip {projected.Id}'s active model handlers require temporal grids "
                    + $"[{string.Join(", ", activeGrids)}], whose compatible grid cannot be "
                    + "represented. Keeping the authored frame count.",
                projected.Id));
            return projected;
        }

        int effectiveFrames;
        try
        {
            effectiveFrames =
                StaticGeneratedFrameGrid.SnapUp(projected.Frames.Value, frameGrid);
        }
        catch (OverflowException)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.temporal-frame-count-overflow",
                $"Clip {projected.Id}'s {projected.Frames.Value}-frame duration cannot be "
                    + $"represented on its resolved {frameGrid}-frame temporal grid. Keeping "
                    + "the authored frame count.",
                projected.Id));
            return projected;
        }

        Dictionary<int, StageSpec> authoredStageByRawIndex =
            (authoredCanonical.Stages ?? [])
                .ToDictionary(stage => stage.ClipStageRawIndex);
        StageSpec[] effectiveStages = projected.Stages
            .Select(stage =>
            {
                RetakeWindowSpec retake = stage.RetakeWindow;
                if (retake is null
                    || !authoredCanonical.Frames.HasValue
                    || !authoredStageByRawIndex.TryGetValue(
                        stage.ClipStageRawIndex,
                        out StageSpec authoredStage)
                    || authoredStage.RetakeWindow is not { } authoredRetake
                    || retake != authoredRetake
                    || authoredRetake.StartFrame < 0
                    || (long)authoredRetake.StartFrame
                        + Math.Max(0, authoredRetake.LengthFrames)
                        < authoredCanonical.Frames.Value)
                {
                    return stage;
                }
                int lengthFrames = Math.Max(
                    retake.LengthFrames,
                    effectiveFrames - retake.StartFrame);
                return stage with
                {
                    RetakeWindow = retake with
                    {
                        LengthFrames = lengthFrames,
                    },
                };
            })
            .ToArray();
        return projected with
        {
            Frames = effectiveFrames,
            Stages = Array.AsReadOnly(effectiveStages),
        };
    }

}
