using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;

namespace VideoStages.Planning;

/// <summary>Post-resolution request values and projection diagnostics.</summary>
internal sealed record EffectiveVideoRequest(
    TimelineSpec Spec,
    IReadOnlyList<PlanDiagnostic> Diagnostics);

/// <summary>
/// Projects resolved temporal grids and reports stale model hints.
/// </summary>
internal static class EffectiveVideoRequestProjection
{
    internal static EffectiveVideoRequest Project(
        TimelineSpec authored,
        ArchitecturePlanningResult architecturePlanning) =>
        Project(authored, RootEnvironment.HostKindFor(authored), architecturePlanning);

    internal static EffectiveVideoRequest Project(
        TimelineSpec authored,
        HostRootKind hostKind,
        ArchitecturePlanningResult architecturePlanning)
    {
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(architecturePlanning);

        ClipSpec[] clips = (authored.Clips ?? []).ToArray();
        int rootTimelineIndex = Array.FindIndex(
            clips,
            clip => clip.InitVideo is null
                && clip.Stages is { Count: > 0 });
        bool rootCanForceTextToVideoGeneration =
            hostKind == HostRootKind.TextToVideo;
        List<PlanDiagnostic> diagnostics = [];
        for (int timelineIndex = 0; timelineIndex < clips.Length; timelineIndex++)
        {
            ClipSpec clip = clips[timelineIndex];
            ClipArchitectureAssignment assignment =
                architecturePlanning.Clips.GetValueOrDefault(clip.Id);
            if (assignment is null)
            {
                continue;
            }
            WarnAboutStaleIdentityHints(clip, assignment, diagnostics);
            if (architecturePlanning.IsBlocked(clip.Id))
            {
                continue;
            }
            clips[timelineIndex] = ProjectResolvedTemporalGrid(
                clip,
                assignment,
                rootCanForceTextToVideoGeneration
                    && timelineIndex == rootTimelineIndex,
                diagnostics);
        }

        return new(
            authored with
            {
                Clips = Array.AsReadOnly(clips),
            },
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    private static void WarnAboutStaleIdentityHints(
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
        ClipSpec clip,
        ClipArchitectureAssignment assignment,
        bool forceRootStageGeneration,
        ICollection<PlanDiagnostic> diagnostics)
    {
        // Runtime-derived lengths skip grid snapping only when the architecture supports them.
        ArchitectureFeature features = assignment.Architecture.Features;
        if (!clip.Frames.HasValue
            || clip.Stages is not { Count: > 0 }
            || (AudioSourceKindPolicy.WantsAudioDerivedLength(clip)
                && features.HasFlag(ArchitectureFeature.AudioDerivedDuration)
                && assignment.Architecture.AudioSourceKinds.Contains(
                    AudioSourceParser.Parse(clip.AudioSource).Kind))
            || (clip.ClipLengthFromControlNet
                && features.HasFlag(ArchitectureFeature.IcLora)))
        {
            return clip;
        }

        List<int> activeGrids = [];
        foreach (StageSpec stage in clip.Stages.Where(stage =>
            !StagePassthroughPolicy.IsPassthrough(
                stage,
                assignment.Architecture)
            || (forceRootStageGeneration && stage.ClipStageIndex == 0)))
        {
            if (!assignment.StageModels.TryGetValue(
                    stage.ClipStageRawIndex,
                    out ResolvedVideoModel resolved))
            {
                // Architecture planning owns the unresolved-stage diagnostic; leave the grid unresolved.
                return clip;
            }
            activeGrids.Add(resolved.FrameGrid);
        }
        if (activeGrids.Count == 0)
        {
            return clip;
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
                $"Clip {clip.Id}'s active model handlers require temporal grids "
                    + $"[{string.Join(", ", activeGrids)}], whose compatible grid cannot be "
                    + "represented. Keeping the authored frame count.",
                clip.Id));
            return clip;
        }

        int effectiveFrames;
        try
        {
            effectiveFrames = StaticGeneratedFrameGrid.SnapUp(
                clip.Frames.Value,
                frameGrid,
                assignment.Architecture.FrameGridOrigin);
        }
        catch (OverflowException)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.temporal-frame-count-overflow",
                $"Clip {clip.Id}'s {clip.Frames.Value}-frame duration cannot be "
                    + $"represented on its resolved {frameGrid}-frame temporal grid. Keeping "
                    + "the authored frame count.",
                clip.Id));
            return clip;
        }

        StageSpec[] effectiveStages = clip.Stages
            .Select(stage =>
            {
                RetakeWindowSpec retake = stage.RetakeWindow;
                if (retake is null
                    || retake.StartFrame < 0
                    || (long)retake.StartFrame
                        + Math.Max(0, retake.LengthFrames)
                        < clip.Frames.Value)
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
        return clip with
        {
            Frames = effectiveFrames,
            Stages = Array.AsReadOnly(effectiveStages),
        };
    }

}
