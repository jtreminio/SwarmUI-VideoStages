using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.Planning;

/// <summary>The graph-free disposition assigned to an authored request value.</summary>
internal enum EffectiveRequestDisposition
{
    Execute,
    IgnoreWithWarning,
    Block,
}

/// <summary>
/// One explicit authored-to-effective request decision. Ignored values carry the warning that is
/// reported through the normal plan diagnostic pipeline.
/// </summary>
internal sealed record EffectiveRequestDecision(
    EffectiveRequestDisposition Disposition,
    string Code,
    string Message,
    int? ClipId = null,
    int? StageId = null,
    int? RawStageIndex = null)
{
    internal static EffectiveRequestDecision Execute(
        string code,
        string message,
        int? clipId = null,
        int? stageId = null,
        int? rawStageIndex = null) =>
        new(
            EffectiveRequestDisposition.Execute,
            code,
            message,
            clipId,
            stageId,
            rawStageIndex);

    internal static EffectiveRequestDecision Ignore(
        string code,
        string message,
        int? clipId = null,
        int? stageId = null,
        int? rawStageIndex = null) =>
        new(
            EffectiveRequestDisposition.IgnoreWithWarning,
            code,
            message,
            clipId,
            stageId,
            rawStageIndex);

    internal static EffectiveRequestDecision Block(
        string code,
        string message,
        int? clipId = null,
        int? stageId = null,
        int? rawStageIndex = null) =>
        new(
            EffectiveRequestDisposition.Block,
            code,
            message,
            clipId,
            stageId,
            rawStageIndex);

    internal PlanDiagnostic ToDiagnostic() => new(
        Disposition == EffectiveRequestDisposition.Block
            ? PlanDiagnosticSeverity.Error
            : PlanDiagnosticSeverity.Warning,
        Code,
        Message,
        ClipId,
        StageId,
        RawStageIndex);
}

/// <summary>One clip and assignment owned by a module in the effective timeline.</summary>
internal sealed record ArchitectureOwnedEffectiveClip(
    int TimelineIndex,
    ClipSpec Clip,
    ClipArchitectureAssignment Assignment);

/// <summary>
/// Graph-free request facts supplied once to each active architecture module.
/// The module sees only the request-global legacy setting it may translate and
/// its owned clips. It returns replacements only for clips whose architecture-private
/// semantics change; common capability projection handles the rest.
/// </summary>
internal sealed record ArchitectureEffectiveRequestProjectionContext(
    LegacyVideoSwapRequestSnapshot LegacyVideoSwap,
    IReadOnlyList<ArchitectureOwnedEffectiveClip> OwnedClips,
    int? AuthoredRootTimelineIndex);

/// <summary>One module-owned clip replacement and its local dispositions.</summary>
internal sealed record ArchitectureProjectedEffectiveClip(
    int TimelineIndex,
    ClipSpec Clip,
    IReadOnlyList<EffectiveRequestDecision> Decisions);

/// <summary>Pure output of one architecture's request-level projection hook.</summary>
internal sealed record ArchitectureEffectiveRequestProjection(
    IReadOnlyList<ArchitectureProjectedEffectiveClip> Clips,
    IReadOnlyList<EffectiveRequestDecision> RequestDecisions);

/// <summary>
/// The only request projection common planning is allowed to consume. The separately cached
/// authored specification remains unchanged for authoring and prompt-tag concerns.
/// </summary>
internal sealed record EffectiveVideoRequest(
    VideoStagesSpec Spec,
    ArchitecturePlanningResult ArchitecturePlanning,
    IReadOnlyList<EffectiveRequestDecision> Decisions,
    IReadOnlyDictionary<int, BoundaryExecutionMode> AuthoredBoundaryModes,
    IReadOnlyDictionary<int, BoundaryFallback> ProjectedBoundaryFallbacks)
{
    internal IReadOnlyList<PlanDiagnostic> Diagnostics =>
        Decisions
            .Where(decision =>
                decision.Disposition != EffectiveRequestDisposition.Execute)
            .Select(decision => decision.ToDiagnostic())
            .ToArray();

    internal IReadOnlySet<int> BlockedClipIds =>
        Decisions
            .Where(decision =>
                decision.Disposition == EffectiveRequestDisposition.Block
                && decision.ClipId.HasValue)
            .Select(decision => decision.ClipId.Value)
            .ToHashSet();
}

/// <summary>
/// Produces one normalized effective request after actual model resolution: canonical identity,
/// architecture-specific policy hooks, capability-driven optional-value omission, and temporal
/// normalization all complete before validation or workflow compilation consumes the request.
/// </summary>
internal static class EffectiveVideoRequestProjector
{
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

        ClipSpec[] authoredClips = (authored.Clips ?? []).ToArray();
        ClipSpec[] canonicalClips = authoredClips.ToArray();
        List<EffectiveRequestDecision>[] canonicalDecisions =
            authoredClips.Select(_ => new List<EffectiveRequestDecision>()).ToArray();
        for (int timelineIndex = 0; timelineIndex < authoredClips.Length; timelineIndex++)
        {
            ClipSpec clip = authoredClips[timelineIndex];
            ClipArchitectureAssignment assignment =
                architecturePlanning.Clips.GetValueOrDefault(clip.Id);
            if (assignment is null)
            {
                continue;
            }
            canonicalClips[timelineIndex] = CanonicalizeResolvedIdentityHints(
                clip,
                assignment,
                canonicalDecisions[timelineIndex]);
        }

        ClipSpec[] effectiveClips = canonicalClips.ToArray();
        List<EffectiveRequestDecision>[] architectureDecisions =
            authoredClips.Select(_ => new List<EffectiveRequestDecision>()).ToArray();
        List<EffectiveRequestDecision> requestDecisions = [];
        int rootTimelineIndex = Array.FindIndex(
            authoredClips,
            clip => clip.SourceVideo is null && clip.Stages is { Count: > 0 });
        RootPlan root = RootPlanCompiler.Compile(
            rootEnvironment,
            authoredClips
                .Where(clip =>
                    clip.SourceVideo is not null
                    || clip.Stages is { Count: > 0 })
                .ToArray());
        bool rootCanForceTextToVideoGeneration =
            root.HostKind == HostRootKind.TextToVideoRoot
            && root.Use == RootUse.Discard
            && !rootEnvironment.HasGlobalRefineSource;
        foreach (ModuleProjectionBatch batch in BuildProjectionBatches(
            effectiveClips,
            architecturePlanning))
        {
            ArchitectureEffectiveRequestProjection projection =
                batch.Projector.ProjectEffectiveRequest(new(
                    authored.LegacyVideoSwap,
                    batch.OwnedClips.AsReadOnly(),
                    rootTimelineIndex >= 0 ? rootTimelineIndex : null));
            ApplyArchitectureProjection(
                batch,
                projection,
                canonicalClips,
                effectiveClips,
                architectureDecisions,
                requestDecisions);
        }
        for (int timelineIndex = 0; timelineIndex < effectiveClips.Length; timelineIndex++)
        {
            ClipSpec clip = effectiveClips[timelineIndex];
            ClipArchitectureAssignment assignment =
                architecturePlanning.Clips.GetValueOrDefault(clip.Id);
            if (assignment is null)
            {
                continue;
            }
            // Architecture hooks see the canonical authored semantics first. This matters for
            // model-sensitive policy such as WAN's terminal-reference check, where an
            // unsupported latent upscale is still active work until common projection removes
            // it. Capability-driven omission then gives every module the same optional-feature
            // behavior before temporal resolution and compilation.
            EffectiveClipProjection common =
                CapabilityDrivenEffectiveRequestProjector.ProjectUnsupportedFeatures(
                    clip,
                    assignment.Architecture,
                    assignment.StageModels);
            effectiveClips[timelineIndex] = common.Clip;
            architectureDecisions[timelineIndex].AddRange(common.Decisions);
        }

        List<EffectiveRequestDecision>[] temporalDecisions =
            authoredClips.Select(_ => new List<EffectiveRequestDecision>()).ToArray();
        for (int timelineIndex = 0; timelineIndex < effectiveClips.Length; timelineIndex++)
        {
            ClipSpec clip = effectiveClips[timelineIndex];
            ClipArchitectureAssignment assignment =
                architecturePlanning.Clips.GetValueOrDefault(clip.Id);
            bool admissionBlocked = architecturePlanning.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == PlanDiagnosticSeverity.Error
                && diagnostic.ClipId == clip.Id)
                || architectureDecisions[timelineIndex].Any(decision =>
                    decision.Disposition == EffectiveRequestDisposition.Block);
            if (assignment is null || admissionBlocked)
            {
                continue;
            }
            effectiveClips[timelineIndex] = ProjectResolvedTemporalGrid(
                canonicalClips[timelineIndex],
                clip,
                assignment,
                rootCanForceTextToVideoGeneration
                    && timelineIndex == rootTimelineIndex,
                temporalDecisions[timelineIndex]);
        }

        List<EffectiveRequestDecision> decisions = [];
        for (int timelineIndex = 0; timelineIndex < authoredClips.Length; timelineIndex++)
        {
            decisions.AddRange(canonicalDecisions[timelineIndex]);
            decisions.AddRange(architectureDecisions[timelineIndex]);
            decisions.AddRange(temporalDecisions[timelineIndex]);
        }
        Dictionary<int, BoundaryExecutionMode> authoredBoundaryModes = [];
        Dictionary<int, BoundaryFallback> projectedBoundaryFallbacks = [];
        ProjectUnsupportedBoundaries(
            effectiveClips,
            architecturePlanning,
            decisions,
            authoredBoundaryModes,
            projectedBoundaryFallbacks);
        decisions.AddRange(requestDecisions);
        return new(
            authored with { Clips = Array.AsReadOnly(effectiveClips) },
            architecturePlanning,
            decisions.AsReadOnly(),
            authoredBoundaryModes,
            projectedBoundaryFallbacks);
    }

    private sealed record ModuleProjectionBatch(
        IArchitectureEffectiveRequestProjector Projector,
        IVideoArchitectureModule Module,
        List<ArchitectureOwnedEffectiveClip> OwnedClips);

    private static IReadOnlyList<ModuleProjectionBatch> BuildProjectionBatches(
        IReadOnlyList<ClipSpec> clips,
        ArchitecturePlanningResult architecturePlanning)
    {
        List<ModuleProjectionBatch> batches = [];
        for (int timelineIndex = 0; timelineIndex < clips.Count; timelineIndex++)
        {
            ClipSpec clip = clips[timelineIndex];
            ClipArchitectureAssignment assignment =
                architecturePlanning.Clips.GetValueOrDefault(clip.Id);
            if (assignment?.Module is not IArchitectureEffectiveRequestProjector projector)
            {
                continue;
            }
            ModuleProjectionBatch batch = batches.SingleOrDefault(
                candidate => ReferenceEquals(candidate.Module, assignment.Module));
            if (batch is null)
            {
                batch = new(projector, assignment.Module, []);
                batches.Add(batch);
            }
            batch.OwnedClips.Add(new(timelineIndex, clip, assignment));
        }
        return batches.AsReadOnly();
    }

    private static void ApplyArchitectureProjection(
        ModuleProjectionBatch batch,
        ArchitectureEffectiveRequestProjection projection,
        IReadOnlyList<ClipSpec> canonicalClips,
        ClipSpec[] effectiveClips,
        IReadOnlyList<List<EffectiveRequestDecision>> architectureDecisions,
        ICollection<EffectiveRequestDecision> requestDecisions)
    {
        if (projection is null)
        {
            throw ProjectionContractError(
                batch.Module,
                "returned a null effective-request projection");
        }
        if (projection.Clips is null
            || projection.RequestDecisions is null)
        {
            throw ProjectionContractError(
                batch.Module,
                "returned null projection collections");
        }
        HashSet<int> ownedIndexes =
            batch.OwnedClips.Select(owned => owned.TimelineIndex).ToHashSet();
        HashSet<int> projectedIndexes = [];
        foreach (ArchitectureProjectedEffectiveClip projected in projection.Clips)
        {
            if (projected is null
                || !ownedIndexes.Contains(projected.TimelineIndex)
                || !projectedIndexes.Add(projected.TimelineIndex))
            {
                throw ProjectionContractError(
                    batch.Module,
                    "returned an unowned, duplicate, or null clip replacement");
            }
            ClipSpec canonical = canonicalClips[projected.TimelineIndex];
            ValidateProjectedTopology(batch.Module, canonical, projected.Clip);
            if (projected.Decisions is null)
            {
                throw ProjectionContractError(
                    batch.Module,
                    $"returned null decisions for clip {canonical.Id}");
            }
            foreach (EffectiveRequestDecision decision in projected.Decisions)
            {
                ValidateDecision(batch.Module, decision);
                if (decision?.ClipId != canonical.Id)
                {
                    throw ProjectionContractError(
                        batch.Module,
                        $"returned a clip decision that does not target clip {canonical.Id}");
                }
                architectureDecisions[projected.TimelineIndex].Add(decision);
            }
            effectiveClips[projected.TimelineIndex] = projected.Clip;
        }
        foreach (EffectiveRequestDecision decision in projection.RequestDecisions)
        {
            ValidateDecision(batch.Module, decision);
            if (decision is null
                || decision.Disposition
                    != EffectiveRequestDisposition.IgnoreWithWarning
                || decision.ClipId.HasValue
                || decision.StageId.HasValue
                || decision.RawStageIndex.HasValue)
            {
                throw ProjectionContractError(
                    batch.Module,
                    "returned a request-global decision that is not an identity-free warning");
            }
            requestDecisions.Add(decision);
        }
    }

    private static void ValidateDecision(
        IVideoArchitectureModule module,
        EffectiveRequestDecision decision)
    {
        if (decision is null
            || !Enum.IsDefined(decision.Disposition)
            || string.IsNullOrWhiteSpace(decision.Code)
            || string.IsNullOrWhiteSpace(decision.Message))
        {
            throw ProjectionContractError(
                module,
                "returned a null or malformed effective-request decision");
        }
    }

    private static void ValidateProjectedTopology(
        IVideoArchitectureModule module,
        ClipSpec canonical,
        ClipSpec projected)
    {
        StageSpec[] canonicalStages = (canonical.Stages ?? []).ToArray();
        StageSpec[] projectedStages = (projected?.Stages ?? []).ToArray();
        bool sameStageTopology =
            canonicalStages.Length == projectedStages.Length
            && canonicalStages.Zip(projectedStages).All(pair =>
                pair.First is not null
                && pair.Second is not null
                && pair.First.Id == pair.Second.Id
                && pair.First.ClipStageIndex == pair.Second.ClipStageIndex
                && pair.First.ClipStageRawIndex == pair.Second.ClipStageRawIndex
                && string.Equals(
                    pair.First.Model,
                    pair.Second.Model,
                    StringComparison.Ordinal));
        if (projected is null
            || projected.Id != canonical.Id
            || projected.SourceVideo != canonical.SourceVideo
            || !sameStageTopology
            || projected.AuthoredArchitectureHint != canonical.AuthoredArchitectureHint
            || projected.AuthoredModelProfileHint != canonical.AuthoredModelProfileHint
            || !(projected.AuthoredStages ?? []).SequenceEqual(
                canonical.AuthoredStages ?? []))
        {
            throw ProjectionContractError(
                module,
                $"changed resolved topology or identity for clip {canonical.Id}");
        }
    }

    private static InvalidOperationException ProjectionContractError(
        IVideoArchitectureModule module,
        string detail) =>
        new(
            $"Video architecture '{module.Descriptor.Id}' {detail}. "
                + "Effective-request projectors may only replace optional values on owned clips.");

    private static ClipSpec CanonicalizeResolvedIdentityHints(
        ClipSpec authored,
        ClipArchitectureAssignment assignment,
        ICollection<EffectiveRequestDecision> decisions)
    {
        string architectureHint = authored.AuthoredArchitectureHint;
        string profileHint = authored.AuthoredModelProfileHint;
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
            if (!string.IsNullOrWhiteSpace(architectureHint)
                && !string.Equals(
                    architectureHint,
                    firstModel.ArchitectureId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                decisions.Add(EffectiveRequestDecision.Ignore(
                    "effective-request.stale-architecture-hint",
                    $"Clip {authored.Id} cached architecture hint '{architectureHint}' does not match "
                        + $"resolved model '{firstModel.ModelName}'. Using "
                        + $"'{firstModel.ArchitectureId}' for this generation.",
                    authored.Id,
                    rawStageIndex: firstRawIndex));
                architectureHint = firstModel.ArchitectureId.Value;
            }
            if (!string.IsNullOrWhiteSpace(profileHint)
                && !string.Equals(
                    profileHint,
                    firstModel.ModelProfileId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                decisions.Add(EffectiveRequestDecision.Ignore(
                    "effective-request.stale-clip-profile-hint",
                    $"Clip {authored.Id} cached model profile '{profileHint}' does not match "
                        + $"resolved model '{firstModel.ModelName}'. Using "
                        + $"'{firstModel.ModelProfileId}' for this generation.",
                    authored.Id,
                    rawStageIndex: firstRawIndex));
                profileHint = firstModel.ModelProfileId.Value;
            }
        }

        AuthoredStageModelSpec[] authoredStages = (authored.AuthoredStages ?? [])
            .Select(stage =>
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
                    return stage;
                }
                decisions.Add(EffectiveRequestDecision.Ignore(
                    "effective-request.stale-stage-profile-hint",
                    $"Clip {authored.Id} authored stage {stage.RawIndex} cached model profile "
                        + $"'{stage.ModelProfileId}', but model '{resolved.ModelName}' resolves "
                        + $"to '{resolved.ModelProfileId}'. Using the resolved profile for this "
                        + "generation.",
                    authored.Id,
                    rawStageIndex: stage.RawIndex));
                return stage with { ModelProfileId = resolved.ModelProfileId.Value };
            })
            .ToArray();
        return authored with
        {
            AuthoredArchitectureHint = architectureHint,
            AuthoredModelProfileHint = profileHint,
            AuthoredStages = Array.AsReadOnly(authoredStages),
        };
    }

    private static ClipSpec ProjectResolvedTemporalGrid(
        ClipSpec authoredCanonical,
        ClipSpec projected,
        ClipArchitectureAssignment assignment,
        bool forceRootStageGeneration,
        ICollection<EffectiveRequestDecision> decisions)
    {
        if (!projected.Frames.HasValue
            || projected.Stages is not { Count: > 0 }
            || projected.ClipLengthFromAudio
            || projected.ClipLengthFromControlNet)
        {
            return projected;
        }

        List<int> activeGrids = [];
        foreach (StageSpec stage in projected.Stages.Where(stage =>
            !stage.IsPassthrough
            || (forceRootStageGeneration && stage.ClipStageIndex == 0)))
        {
            if (!assignment.StageModels.TryGetValue(
                    stage.ClipStageRawIndex,
                    out ResolvedVideoModel resolved))
            {
                // Architecture planning already reports the unresolved stage. Do not guess a grid
                // or duplicate that admission error here.
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
            decisions.Add(EffectiveRequestDecision.Block(
                "effective-request.temporal-grid-conflict",
                $"Clip {projected.Id}'s active model handlers require temporal grids "
                    + $"[{string.Join(", ", activeGrids)}], whose compatible grid cannot be "
                    + "represented. Use stage models with compatible temporal requirements.",
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
            decisions.Add(EffectiveRequestDecision.Block(
                "effective-request.temporal-frame-count-overflow",
                $"Clip {projected.Id}'s {projected.Frames.Value}-frame duration cannot be "
                    + $"represented on its resolved {frameGrid}-frame temporal grid.",
                projected.Id));
            return projected;
        }

        Dictionary<int, StageSpec> authoredStageByRawIndex =
            (authoredCanonical.Stages ?? [])
                .ToDictionary(stage => stage.ClipStageRawIndex);
        bool retakeEndAdjusted = false;
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
                retakeEndAdjusted |= lengthFrames != retake.LengthFrames;
                return stage with
                {
                    RetakeWindow = retake with
                    {
                        LengthFrames = lengthFrames,
                    },
                };
            })
            .ToArray();
        if (effectiveFrames != projected.Frames.Value)
        {
            string provenance = authoredCanonical.Frames == projected.Frames
                ? $"its authored {projected.Frames.Value}-frame duration"
                : $"its authored {authoredCanonical.Frames?.ToString() ?? "unknown"}-frame "
                    + $"duration was architecture-projected to {projected.Frames.Value} frames and";
            decisions.Add(EffectiveRequestDecision.Execute(
                "effective-request.temporal-grid",
                $"Clip {projected.Id} resolves to a {frameGrid}-frame temporal grid; "
                    + $"{provenance} executes as {effectiveFrames} frames.",
                projected.Id));
        }
        else if (retakeEndAdjusted)
        {
            decisions.Add(EffectiveRequestDecision.Execute(
                "effective-request.temporal-retake-end",
                $"Clip {projected.Id}'s authored full-length retake follows the "
                    + $"{effectiveFrames}-frame duration produced by architecture projection.",
                projected.Id));
        }
        return projected with
        {
            Frames = effectiveFrames,
            Stages = Array.AsReadOnly(effectiveStages),
        };
    }

    private static void ProjectUnsupportedBoundaries(
        ClipSpec[] clips,
        ArchitecturePlanningResult architecturePlanning,
        ICollection<EffectiveRequestDecision> decisions,
        IDictionary<int, BoundaryExecutionMode> authoredBoundaryModes,
        IDictionary<int, BoundaryFallback> projectedBoundaryFallbacks)
    {
        int[] executableIndexes = clips
            .Select((clip, index) => (clip, index))
            .Where(item => item.clip is not null
                && (item.clip.SourceVideo is not null
                    || item.clip.Stages is { Count: > 0 }))
            .Select(item => item.index)
            .ToArray();
        for (int position = 0; position < executableIndexes.Length - 1; position++)
        {
            int fromIndex = executableIndexes[position];
            int toIndex = executableIndexes[position + 1];
            ClipSpec sourceClip = clips[fromIndex];
            BoundaryExecutionMode requested =
                BoundaryPolicy.ParsePlanMode(sourceClip.BoundaryOut, out bool known);
            if (!known
                || requested == BoundaryExecutionMode.Cut)
            {
                continue;
            }
            ClipArchitectureAssignment fromAssignment =
                architecturePlanning.Clips.GetValueOrDefault(sourceClip.Id);
            ClipArchitectureAssignment toAssignment =
                architecturePlanning.Clips.GetValueOrDefault(clips[toIndex].Id);
            if (fromAssignment is null || toAssignment is null)
            {
                continue;
            }
            ArchitectureBoundaryModePolicy policy = fromAssignment.Architecture
                .BoundaryPolicy.Modes.GetValueOrDefault(requested);
            bool crossesArchitectures =
                fromAssignment.Architecture.Id != toAssignment.Architecture.Id;
            if (!crossesArchitectures
                && policy is not { Support: RuleSupport.Unsupported })
            {
                decisions.Add(EffectiveRequestDecision.Execute(
                    "effective-request.boundary",
                    $"Clip {sourceClip.Id} executes its '{sourceClip.BoundaryOut}' boundary.",
                    sourceClip.Id));
                continue;
            }
            string reason = crossesArchitectures
                ? "the adjacent clips use different architectures"
                : policy.Reason;
            decisions.Add(EffectiveRequestDecision.Ignore(
                "effective-request.boundary-degraded-to-cut",
                $"Clip {sourceClip.Id} boundary '{sourceClip.BoundaryOut}' cannot execute because {reason}. "
                    + "The authored boundary remains saved, but this generation uses a cut.",
                sourceClip.Id));
            authoredBoundaryModes[sourceClip.Id] = requested;
            projectedBoundaryFallbacks[sourceClip.Id] =
                BoundaryFallback.ArchitectureRuleUnsupported;
            clips[fromIndex] = sourceClip with
            {
                BoundaryOut = Constants.BoundaryOutCut,
                BoundaryOutOverlap = 0,
                BoundaryOutCarryAudio = false,
            };
        }
    }

}
