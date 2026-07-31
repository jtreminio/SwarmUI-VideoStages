using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.Planning;

/// <summary>The graph-free disposition assigned to an authored request value.</summary>
internal enum EffectiveRequestDisposition
{
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
/// The only projected request values common planning is allowed to consume. Resolved architecture
/// assignments remain the caller-owned input because projection preserves model and topology keys.
/// The separately cached authored specification remains unchanged for authoring and prompt-tag
/// concerns.
/// </summary>
internal sealed record EffectiveVideoRequest(
    VideoStagesSpec Spec,
    IReadOnlyList<EffectiveRequestDecision> Decisions,
    IReadOnlyDictionary<int, BoundaryJoinType> AuthoredBoundaryModes,
    IReadOnlyDictionary<int, BoundaryFallbackReason> ProjectedBoundaryFallbacks)
{
    internal IReadOnlyList<PlanDiagnostic> Diagnostics =>
        Decisions
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
    /// <summary>
    /// Owns the authored, canonical, and progressively projected forms of one timeline clip.
    /// Decisions stay beside the clip they describe instead of being coordinated through
    /// parallel arrays for each projection phase.
    /// </summary>
    private sealed class EffectiveClipPlanningContext(
        int timelineIndex,
        ClipSpec authored,
        ClipArchitectureAssignment assignment)
    {
        internal int TimelineIndex { get; } = timelineIndex;
        internal ClipSpec Authored { get; } = authored;
        internal ClipArchitectureAssignment Assignment { get; } = assignment;
        internal ClipSpec Canonical { get; set; } = authored;
        internal ClipSpec Effective { get; set; } = authored;
        internal List<EffectiveRequestDecision> Decisions { get; } = [];
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
            clip.Canonical = CanonicalizeResolvedIdentityHints(
                clip.Authored,
                clip.Assignment,
                clip.Decisions);
            clip.Effective = clip.Canonical;
        }

        List<EffectiveRequestDecision> requestDecisions = [];
        int rootTimelineIndex = Array.FindIndex(
            clips,
            clip => clip.Authored.InitVideo is null
                && clip.Authored.Stages is { Count: > 0 });
        bool rootCanForceTextToVideoGeneration =
            rootEnvironment.HostKind == HostRootKind.TextToVideoRoot;
        foreach (ModuleProjectionBatch batch in BuildProjectionBatches(
            clips))
        {
            ArchitectureEffectiveRequestProjection projection =
                batch.Projector.ProjectEffectiveRequest(new(
                    authored.LegacyVideoSwap,
                    batch.OwnedClips.AsReadOnly(),
                    rootTimelineIndex >= 0 ? rootTimelineIndex : null));
            ApplyArchitectureProjection(
                batch,
                projection,
                clips,
                requestDecisions);
        }
        foreach (EffectiveClipPlanningContext clip in clips)
        {
            if (clip.Assignment is null)
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
                    clip.Effective,
                    clip.Assignment.Architecture);
            clip.Effective = common.Clip;
            clip.Decisions.AddRange(common.Decisions);
        }

        foreach (EffectiveClipPlanningContext clip in clips)
        {
            bool admissionBlocked = architecturePlanning.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == PlanDiagnosticSeverity.Error
                && diagnostic.ClipId == clip.Effective.Id)
                || clip.Decisions.Any(decision =>
                    decision.Disposition == EffectiveRequestDisposition.Block);
            if (clip.Assignment is null || admissionBlocked)
            {
                continue;
            }
            clip.Effective = ProjectResolvedTemporalGrid(
                clip.Canonical,
                clip.Effective,
                clip.Assignment,
                rootCanForceTextToVideoGeneration
                    && clip.TimelineIndex == rootTimelineIndex,
                clip.Decisions);
        }

        List<EffectiveRequestDecision> decisions =
            clips.SelectMany(clip => clip.Decisions).ToList();
        Dictionary<int, BoundaryJoinType> authoredBoundaryModes = [];
        Dictionary<int, BoundaryFallbackReason> projectedBoundaryFallbacks = [];
        ProjectUnsupportedBoundaries(
            clips,
            decisions,
            authoredBoundaryModes,
            projectedBoundaryFallbacks);
        decisions.AddRange(requestDecisions);
        return new(
            authored with
            {
                Clips = Array.AsReadOnly(clips.Select(clip => clip.Effective).ToArray()),
            },
            decisions.AsReadOnly(),
            authoredBoundaryModes,
            projectedBoundaryFallbacks);
    }

    private sealed record ModuleProjectionBatch(
        IArchitectureEffectiveRequestProjector Projector,
        IVideoArchitectureModule Module,
        List<ArchitectureOwnedEffectiveClip> OwnedClips);

    private static IReadOnlyList<ModuleProjectionBatch> BuildProjectionBatches(
        IReadOnlyList<EffectiveClipPlanningContext> clips)
    {
        List<ModuleProjectionBatch> batches = [];
        foreach (EffectiveClipPlanningContext clip in clips)
        {
            if (clip.Assignment?.Module
                is not IArchitectureEffectiveRequestProjector projector)
            {
                continue;
            }
            ModuleProjectionBatch batch = batches.SingleOrDefault(
                candidate => ReferenceEquals(candidate.Module, clip.Assignment.Module));
            if (batch is null)
            {
                batch = new(projector, clip.Assignment.Module, []);
                batches.Add(batch);
            }
            batch.OwnedClips.Add(new(
                clip.TimelineIndex,
                clip.Effective,
                clip.Assignment));
        }
        return batches.AsReadOnly();
    }

    private static void ApplyArchitectureProjection(
        ModuleProjectionBatch batch,
        ArchitectureEffectiveRequestProjection projection,
        IReadOnlyList<EffectiveClipPlanningContext> clips,
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
            EffectiveClipPlanningContext clip = clips[projected.TimelineIndex];
            ClipSpec canonical = clip.Canonical;
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
                clip.Decisions.Add(decision);
            }
            clip.Effective = projected.Clip;
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
            || projected.InitVideo != canonical.InitVideo
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

    private static void ProjectUnsupportedBoundaries(
        IReadOnlyList<EffectiveClipPlanningContext> clips,
        ICollection<EffectiveRequestDecision> decisions,
        IDictionary<int, BoundaryJoinType> authoredBoundaryModes,
        IDictionary<int, BoundaryFallbackReason> projectedBoundaryFallbacks)
    {
        int[] executableIndexes = clips
            .Select((clip, index) => (clip, index))
            .Where(item =>
                item.clip.Effective.InitVideo is not null
                || item.clip.Effective.Stages is { Count: > 0 })
            .Select(item => item.index)
            .ToArray();
        for (int position = 0; position < executableIndexes.Length - 1; position++)
        {
            int fromIndex = executableIndexes[position];
            int toIndex = executableIndexes[position + 1];
            EffectiveClipPlanningContext source = clips[fromIndex];
            EffectiveClipPlanningContext target = clips[toIndex];
            ClipSpec sourceClip = source.Effective;
            BoundaryJoinType requested =
                BoundaryPolicy.ParsePlanMode(sourceClip.BoundaryOut, out bool known);
            if (!known
                || requested == BoundaryJoinType.Cut)
            {
                continue;
            }
            ClipArchitectureAssignment fromAssignment = source.Assignment;
            ClipArchitectureAssignment toAssignment = target.Assignment;
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
                BoundaryFallbackReason.ArchitectureRuleUnsupported;
            source.Effective = sourceClip with
            {
                BoundaryOut = Constants.BoundaryOutCut,
                BoundaryOutOverlap = 0,
                BoundaryOutCarryAudio = false,
            };
        }
    }

}
