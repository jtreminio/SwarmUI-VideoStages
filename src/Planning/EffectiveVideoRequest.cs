using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.HostVideo;
using VideoStages.Architectures.Wan;

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
/// Projects the first safely ignorable policy slice after actual model resolution and before any
/// common planning or workflow mutation.
/// </summary>
internal static class EffectiveVideoRequestProjector
{
    internal static EffectiveVideoRequest Project(
        VideoStagesSpec authored,
        ArchitecturePlanningResult architecturePlanning)
    {
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(architecturePlanning);

        List<EffectiveRequestDecision> decisions = [];
        Dictionary<int, BoundaryExecutionMode> authoredBoundaryModes = [];
        Dictionary<int, BoundaryFallback> projectedBoundaryFallbacks = [];
        ClipSpec[] effectiveClips = (authored.Clips ?? [])
            .Select(clip => ProjectClip(clip, architecturePlanning, decisions))
            .ToArray();
        ProjectUnsupportedBoundaries(
            effectiveClips,
            architecturePlanning,
            decisions,
            authoredBoundaryModes,
            projectedBoundaryFallbacks);
        ProjectLegacyWanSwap(authored, architecturePlanning, decisions);
        ProjectLegacyHostVideoSwap(authored, architecturePlanning, decisions);
        return new(
            authored with { Clips = Array.AsReadOnly(effectiveClips) },
            architecturePlanning,
            decisions.AsReadOnly(),
            authoredBoundaryModes,
            projectedBoundaryFallbacks);
    }

    private static void ProjectLegacyHostVideoSwap(
        VideoStagesSpec authored,
        ArchitecturePlanningResult architecturePlanning,
        ICollection<EffectiveRequestDecision> decisions)
    {
        if (authored.LegacyVideoSwap?.IsConfigured != true
            || ResolveAuthoredRootOwner(authored, architecturePlanning)
                != HostVideoArchitectureModule.ArchitectureId)
        {
            return;
        }

        decisions.Add(Ignore(
            "effective-request.host-video-swap-ignored",
            "Generic VideoStages ignores SwarmUI's request-global Video Swap Model, Video Swap "
                + "Percent, and Video Swap section settings. The authored values remain in "
                + "request metadata. Create separate timeline stages instead."));
    }

    private static void ProjectLegacyWanSwap(
        VideoStagesSpec authored,
        ArchitecturePlanningResult architecturePlanning,
        ICollection<EffectiveRequestDecision> decisions)
    {
        if (authored.LegacyVideoSwap?.IsConfigured != true
            || !architecturePlanning.Clips.Values.Any(
                assignment =>
                    assignment?.Architecture?.Id == WanArchitectureModule.ArchitectureId))
        {
            return;
        }

        decisions.Add(Ignore(
            "effective-request.wan-video-swap-ignored",
            "WAN VideoStages ignores SwarmUI's request-global Video Swap Model, Video Swap "
                + "Percent, and Video Swap section settings. The authored values remain in "
                + "request metadata. Create separate high-noise and low-noise timeline stages "
                + "instead."));
    }

    private static ClipSpec ProjectClip(
        ClipSpec authored,
        ArchitecturePlanningResult architecturePlanning,
        ICollection<EffectiveRequestDecision> decisions)
    {
        ClipArchitectureAssignment assignment =
            architecturePlanning.Clips.GetValueOrDefault(authored.Id);
        if (assignment is null)
        {
            return authored;
        }

        ClipSpec effective = CanonicalizeResolvedIdentityHints(
            authored,
            assignment,
            decisions);
        bool isWan =
            assignment.Architecture.Id == WanArchitectureModule.ArchitectureId;
        bool isHostVideo =
            assignment.Architecture.Id == HostVideoArchitectureModule.ArchitectureId;
        if (!isWan && !isHostVideo)
        {
            return effective;
        }
        if (isHostVideo)
        {
            effective = ProjectBaselineVideoClip(
                effective,
                decisions,
                preserveFrameReferences: false,
                HostVideoArchitectureModule.Instance.Descriptor,
                "generic host-video",
                "host-video");
        }
        else
        {
            effective = ProjectBaselineVideoClip(
                effective,
                decisions,
                preserveFrameReferences: true,
                WanArchitectureModule.Instance.Descriptor,
                "WAN",
                "wan");
            effective = ProjectWanFrameReferences(effective, assignment, decisions);
        }
        string architectureName = isWan ? "WAN" : "generic host video";
        string codePrefix = isWan ? "wan" : "host-video";

        bool hasIcLoraData = effective.IcLoras is { Count: > 0 }
            || (effective.Stages?.Any(stage => stage.IcLoraStrengths is { Count: > 0 })
                == true);
        if (hasIcLoraData)
        {
            decisions.Add(Ignore(
                $"effective-request.{codePrefix}-ic-lora-ignored",
                $"Clip {effective.Id} configures IC-LoRA data, but {architectureName} does not support "
                    + "IC-LoRA. The setting remains authored and is ignored for this generation.",
                effective.Id));
            effective = effective with
            {
                IcLoras = [],
                Stages = effective.Stages?
                    .Select(stage => stage with { IcLoraStrengths = [] })
                    .ToArray(),
            };
        }

        if (effective.Stages is not { Count: > 0 })
        {
            return effective;
        }
        StageSpec[] stages = effective.Stages.ToArray();
        for (int index = 0; index < stages.Length; index++)
        {
            StageSpec stage = stages[index];
            if (stage.Upscale == 1)
            {
                continue;
            }
            if (stage.IsPixelUpscale)
            {
                decisions.Add(Execute(
                    $"effective-request.{codePrefix}-pixel-upscale",
                    $"Clip {effective.Id} Stage {stage.Id} executes its pixel upscale.",
                    effective.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
                continue;
            }
            if (!stage.IsModelUpscale
                && !stage.IsLatentUpscale
                && !stage.IsLatentModelUpscale)
            {
                decisions.Add(Block(
                    "effective-request.unknown-upscale",
                    $"Clip {effective.Id} Stage {stage.Id} uses unknown upscale mode "
                        + $"'{stage.UpscaleMethod}'.",
                    effective.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
                continue;
            }
            decisions.Add(Ignore(
                $"effective-request.{codePrefix}-advanced-upscale-ignored",
                $"Clip {effective.Id} Stage {stage.Id} requests unsupported {architectureName} upscale "
                    + $"method '{stage.UpscaleMethod}'. The authored setting remains saved, "
                    + "but this generation runs the stage at 1×.",
                effective.Id,
                stage.Id,
                stage.ClipStageRawIndex));
            stages[index] = stage with
            {
                Upscale = 1,
                UpscaleMethod = "pixel-lanczos",
            };
        }
        return effective with { Stages = Array.AsReadOnly(stages) };
    }

    private static ClipSpec ProjectWanFrameReferences(
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
                decisions.Add(Ignore(
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
                decisions.Add(Ignore(
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
                    decisions.Add(Ignore(
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
                decisions.Add(Ignore(
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
                decisions.Add(Ignore(
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
                    decisions.Add(Ignore(
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

    private static ClipSpec ProjectBaselineVideoClip(
        ClipSpec effective,
        ICollection<EffectiveRequestDecision> decisions,
        bool preserveFrameReferences,
        VideoArchitectureDescriptor descriptor,
        string architectureName,
        string codePrefix)
    {
        void IgnoreConfigured(bool configured, string feature, string code)
        {
            if (!configured)
            {
                return;
            }
            decisions.Add(Ignore(
                $"effective-request.{codePrefix}-{code}-ignored",
                $"Clip {effective.Id} configures {feature}, which {architectureName} "
                    + "does not use. The authored setting remains saved and is ignored "
                    + "for this generation.",
                effective.Id));
        }

        if (!preserveFrameReferences)
        {
            IgnoreConfigured(
                effective.ImageRefs is { Count: > 0 }
                    || effective.Stages?.Any(
                        stage => stage.ImageRefStrengths is { Count: > 0 }) == true,
                "image/frame references",
                "references");
        }
        else
        {
            IgnoreConfigured(
                effective.Stages?.Any(
                    stage => stage.ImageRefStrengths?.Any(
                        strength => Math.Abs(
                            strength - Constants.DefaultStageRefStrength)
                            > 0.000001) == true) == true,
                "per-stage frame-reference strengths",
                "reference-strengths");
        }
        IgnoreConfigured(
            effective.PromptWindows is { Count: > 0 },
            "prompt relay windows",
            "prompt-relay");
        IgnoreConfigured(
            effective.Stages?.Any(stage => stage.RetakeWindow is not null) == true,
            "a retake window",
            "retake");
        IgnoreConfigured(
            effective.Stages?.Any(stage => stage.ControlNetStrength.HasValue) == true,
            "stage ControlNet strength",
            "controlnet");
        IgnoreConfigured(
            effective.UploadedAudio is not null
                || !string.Equals(
                    effective.AudioSource,
                    Constants.AudioSourceNative,
                    StringComparison.OrdinalIgnoreCase),
            "a clip audio source",
            "audio-source");
        IgnoreConfigured(
            effective.SaveAudioTrack,
            "standalone audio output",
            "audio-output");
        IgnoreConfigured(
            effective.ClipLengthFromAudio,
            "audio-derived clip duration",
            "audio-duration");
        IgnoreConfigured(
            effective.ClipLengthFromControlNet,
            "control-derived clip duration",
            "control-duration");
        IgnoreConfigured(
            effective.ReuseAudio,
            "captured stage audio reuse",
            "audio-reuse");
        IgnoreConfigured(
            effective.BoundaryOutCarryAudio,
            "audio boundary carry",
            "audio-boundary");
        IgnoreConfigured(
            effective.ReferenceFraming != ReferenceFramingMode.Crop,
            "non-default reference framing",
            "reference-framing");

        StageSpec[] stages = (effective.Stages ?? [])
            .Select((stage, index) =>
            {
                StageGuideReferenceSelection guide =
                    StageGuideReferencePolicy.Classify(stage.ImageReference);
                bool supportedGuide =
                    descriptor.StageGuideReferences.Allows(guide);
                if (!supportedGuide)
                {
                    decisions.Add(Ignore(
                        $"effective-request.{codePrefix}-stage-reference-ignored",
                        $"Clip {effective.Id} Stage {stage.Id} uses image selector "
                            + $"'{stage.ImageReference}', which {architectureName} "
                            + "does not use. The authored selector remains saved; this generation "
                            + $"uses '{(index == 0 ? "Generated" : "PreviousStage")}'.",
                        effective.Id,
                        stage.Id,
                        stage.ClipStageRawIndex));
                }
                return stage with
                {
                    ImageReference = supportedGuide
                        ? stage.ImageReference
                        : index == 0 ? "Generated" : "PreviousStage",
                    ImageRefStrengths = [],
                    RetakeWindow = null,
                    ControlNetStrength = null,
                };
            })
            .ToArray();
        return effective with
        {
            AudioSource = Constants.AudioSourceNative,
            SaveAudioTrack = false,
            ClipLengthFromAudio = false,
            ClipLengthFromControlNet = false,
            ReuseAudio = false,
            UploadedAudio = null,
            ImageRefs = preserveFrameReferences ? effective.ImageRefs : [],
            PromptWindows = [],
            BoundaryOutCarryAudio = false,
            ReferenceFraming = ReferenceFramingMode.Crop,
            Stages = Array.AsReadOnly(stages),
        };
    }

    private static ArchitectureId? ResolveAuthoredRootOwner(
        VideoStagesSpec authored,
        ArchitecturePlanningResult architecturePlanning)
    {
        ClipSpec root = (authored.Clips ?? []).FirstOrDefault(
            clip => clip.SourceVideo is null && clip.Stages is { Count: > 0 });
        return root is null
            ? null
            : architecturePlanning.Clips.GetValueOrDefault(root.Id)
                ?.Architecture?.Id;
    }

    private static ClipSpec CanonicalizeResolvedIdentityHints(
        ClipSpec authored,
        ClipArchitectureAssignment assignment,
        ICollection<EffectiveRequestDecision> decisions)
    {
        string architectureHint = authored.AuthoredArchitectureId;
        string profileHint = authored.AuthoredModelProfileId;
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
                decisions.Add(Ignore(
                    "effective-request.stale-architecture-hint",
                    $"Clip {authored.Id} cached architecture '{architectureHint}' does not match "
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
                decisions.Add(Ignore(
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
                decisions.Add(Ignore(
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
            AuthoredArchitectureId = architectureHint,
            AuthoredModelProfileId = profileHint,
            AuthoredStages = Array.AsReadOnly(authoredStages),
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
                decisions.Add(Execute(
                    "effective-request.boundary",
                    $"Clip {sourceClip.Id} executes its '{sourceClip.BoundaryOut}' boundary.",
                    sourceClip.Id));
                continue;
            }
            string reason = crossesArchitectures
                ? "the adjacent clips use different architectures"
                : policy.Reason;
            decisions.Add(Ignore(
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

    private static EffectiveRequestDecision Execute(
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

    private static EffectiveRequestDecision Ignore(
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

    private static EffectiveRequestDecision Block(
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
}
