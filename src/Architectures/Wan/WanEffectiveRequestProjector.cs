using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// WAN-owned, graph-free request projection. Shared limited-video behavior is
/// composed around WAN's model-sensitive bounded-frame reference policy.
/// </summary>
internal static class WanEffectiveRequestProjector
{
    internal static ArchitectureEffectiveRequestProjection Project(
        ArchitectureEffectiveRequestProjectionContext context,
        VideoArchitectureDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptor);

        ArchitectureProjectedEffectiveClip[] clips = context.OwnedClips
            .Select(owned => ProjectClip(owned, descriptor))
            .ToArray();
        EffectiveRequestDecision[] requestDecisions =
            context.LegacyVideoSwap?.IsConfigured == true
                && context.OwnedClips.Count > 0
            ?
            [
                EffectiveRequestDecision.Ignore(
                    "effective-request.wan-video-swap-ignored",
                    "WAN VideoStages ignores SwarmUI's request-global Video Swap Model, Video "
                        + "Swap Percent, and Video Swap section settings. The authored values "
                        + "remain in request metadata. Create separate high-noise and low-noise "
                        + "timeline stages instead."),
            ]
            : [];
        return new(
            Array.AsReadOnly(clips),
            Array.AsReadOnly(requestDecisions));
    }

    private static ArchitectureProjectedEffectiveClip ProjectClip(
        ArchitectureOwnedEffectiveClip owned,
        VideoArchitectureDescriptor descriptor)
    {
        EffectiveClipProjection baseline =
            BaselineVideoEffectiveRequestProjector.ProjectBaseline(
                owned.Clip,
                preserveFrameReferences: true,
                descriptor,
                "WAN",
                "wan");
        List<EffectiveRequestDecision> decisions = [.. baseline.Decisions];
        ClipSpec effective = ProjectFrameReferences(
            baseline.Clip,
            owned.Assignment,
            decisions);
        EffectiveClipProjection enhancements =
            BaselineVideoEffectiveRequestProjector.ProjectUnsupportedEnhancements(
                effective,
                "WAN",
                "wan");
        decisions.AddRange(enhancements.Decisions);
        return new(
            owned.TimelineIndex,
            enhancements.Clip,
            decisions.AsReadOnly());
    }

    private static ClipSpec ProjectFrameReferences(
        ClipSpec effective,
        ClipArchitectureAssignment assignment,
        ICollection<EffectiveRequestDecision> decisions)
    {
        List<ImageRefSpec> retained = [];
        bool hasFirst = false;
        bool hasLast = false;
        foreach (ImageRefSpec reference in effective.ImageRefs ?? [])
        {
            bool first = !reference.FromEnd && reference.Frame == 1;
            bool last = reference.FromEnd && reference.Frame == 1;
            if (!first && !last)
            {
                decisions.Add(EffectiveRequestDecision.Ignore(
                    "effective-request.wan-middle-frame-reference-ignored",
                    $"Clip {effective.Id} has a WAN image reference at "
                        + $"{(reference.FromEnd ? "end-relative" : "start-relative")} frame "
                        + $"{reference.Frame}. WAN native conditioning accepts only the first "
                        + "and final frame. The authored reference remains saved and is ignored "
                        + "for this generation.",
                    effective.Id));
                continue;
            }
            if (first && effective.SourceVideo is not null)
            {
                decisions.Add(EffectiveRequestDecision.Ignore(
                    "effective-request.wan-sourced-first-frame-reference-ignored",
                    $"Clip {effective.Id} already enters from sourced video, so its separate "
                        + "first-frame reference remains saved and is ignored for this "
                        + "generation.",
                    effective.Id));
                continue;
            }
            if (first)
            {
                StageSpec firstStage = (effective.Stages ?? []).FirstOrDefault();
                ResolvedVideoModel firstModel =
                    firstStage is null
                        ? null
                        : assignment.StageModels.GetValueOrDefault(
                            firstStage.ClipStageRawIndex);
                if (firstModel?.ReferencePositions?.Contains(
                        "first",
                        StringComparer.Ordinal) != true)
                {
                    decisions.Add(EffectiveRequestDecision.Ignore(
                        "effective-request.wan-first-frame-reference-ignored",
                        $"Clip {effective.Id}'s first-frame reference is not supported by the "
                            + $"selected first WAN model '{firstModel?.ModelName ?? "<missing>"}'. "
                            + "The authored reference remains saved and is ignored for this "
                            + "generation.",
                        effective.Id));
                    continue;
                }
            }
            if (!StringUtils.Equals(reference.Source, "Upload"))
            {
                decisions.Add(EffectiveRequestDecision.Ignore(
                    "effective-request.wan-frame-reference-source-ignored",
                    $"Clip {effective.Id}'s WAN {(first ? "first" : "final")}-frame "
                        + $"reference uses source '{reference.Source}', but the native WAN "
                        + "bounded-reference path currently accepts uploaded images. The "
                        + "authored reference remains saved and is ignored for this generation.",
                    effective.Id));
                continue;
            }
            if (first && hasFirst || last && hasLast)
            {
                decisions.Add(EffectiveRequestDecision.Ignore(
                    "effective-request.wan-duplicate-frame-reference-ignored",
                    $"Clip {effective.Id} has more than one WAN "
                        + $"{(first ? "first" : "last")} frame reference. The first authored "
                        + "reference remains active; later references remain saved and are "
                        + "ignored for this generation.",
                    effective.Id));
                continue;
            }
            if (last)
            {
                StageSpec terminalStage = (effective.Stages ?? [])
                    .LastOrDefault(stage => !stage.IsPassthrough);
                ResolvedVideoModel terminal =
                    terminalStage is null
                        ? null
                        : assignment.StageModels.GetValueOrDefault(
                            terminalStage.ClipStageRawIndex);
                if (terminal?.ReferencePositions?.Contains(
                        "last",
                        StringComparer.Ordinal) != true)
                {
                    decisions.Add(EffectiveRequestDecision.Ignore(
                        "effective-request.wan-last-frame-reference-ignored",
                        $"Clip {effective.Id}'s final-frame reference is not supported by the "
                            + $"selected terminal WAN model '{terminal?.ModelName ?? "<missing>"}'. "
                            + "The authored reference remains saved and is ignored for this "
                            + "generation.",
                        effective.Id));
                    continue;
                }
            }
            retained.Add(reference);
            hasFirst |= first;
            hasLast |= last;
        }
        return effective with { ImageRefs = retained.AsReadOnly() };
    }
}
