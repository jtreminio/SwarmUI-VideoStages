using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>
/// Single source of truth for LTX conditional behavior. The same typed policies publish catalog
/// rules and produce backend diagnostics.
/// </summary>
internal static class Ltx2ConditionalRulePolicySource
{
    internal static RuleDecision AudioReuseRequiresThreeStages { get; } =
        RuleDecision.Conditional(
            "audio.reuse.requires_three_stages",
            "Audio reuse needs at least three active stages: generate, capture, then reuse.",
            RuleScope.Clip,
            new MinimumActiveStagesRuleConstraints(
                3,
                RuleFailureSeverity.Warning,
                RuleFailureEffect.DisableFeature));

    internal static RuleDecision PromptRelayRequiresFixedLength { get; } =
        RuleDecision.Conditional(
            "prompt-relay-dynamic-length-unsupported",
            "Prompt relay requires a fixed frame count and cannot be combined with audio-owned or ControlNet-owned clip length.",
            RuleScope.Clip,
            new FixedFrameCountRuleConstraints(true));

    internal static RuleDecision RetakeAndReferencesAreExclusive { get; } =
        RuleDecision.Conditional(
            "retake-frame-references-unsupported",
            "Retake and frame references are mutually exclusive because guide merging would overwrite the retake mask.",
            RuleScope.Stage,
            new MutuallyExclusiveRuleConstraints(
                [ConditionalRuleFeature.Retake, ConditionalRuleFeature.FrameReferences]));

    internal static RuleDecision RetakeRequiresSource { get; } =
        RuleDecision.Conditional(
            "retake-source-required",
            "Retake requires a sourced clip or a global Refine Video source.",
            RuleScope.Clip,
            new RequiredEntryModesRuleConstraints(
                [ArchitectureEntryMode.SourceVideo, ArchitectureEntryMode.RefineVideo]));

    internal static RuleDecision HdrRequiresUniformTimeline { get; } =
        RuleDecision.Conditional(
            "mixed-hdr-timeline-unsupported",
            "HDR IC-LoRA activation must be uniform across the complete timeline.",
            RuleScope.Architecture,
            new UniformTimelineFeatureRuleConstraints(
                ConditionalRuleFeature.Hdr,
                2));

    internal static IReadOnlyList<RuleDecision> PublishedRules { get; } =
    [
        AudioReuseRequiresThreeStages,
        PromptRelayRequiresFixedLength,
        RetakeAndReferencesAreExclusive,
        RetakeRequiresSource,
        HdrRequiresUniformTimeline,
    ];

    internal static IReadOnlyList<VideoPlanDiagnostic> Validate(
        IReadOnlyList<ClipPlan> clips,
        IReadOnlyList<ClipPlan> timelineClips,
        RootPlan root)
    {
        List<VideoPlanDiagnostic> diagnostics = [];
        foreach (ClipPlan clip in clips)
        {
            if (clip.ArchitecturePayload is Ltx2ClipPayload ltxClip
                && ltxClip.AudioReuse.IsRequested
                && !ltxClip.AudioReuse.IsEligible)
            {
                diagnostics.Add(Diagnostic(
                    AudioReuseRequiresThreeStages,
                    VideoPlanDiagnosticSeverity.Warning,
                    clip.ClipId));
            }
            if (clip.Audio.Length.Owner is AudioLengthOwner.Audio or AudioLengthOwner.ControlNet
                && clip.Stages.Any(stage =>
                    stage.ArchitecturePayload is Ltx2StagePayload payload
                    && !payload.PromptRelay.AuthoredWindows.IsDefaultOrEmpty))
            {
                diagnostics.Add(Error(PromptRelayRequiresFixedLength, clip.ClipId));
            }

            foreach (StagePlan stage in clip.Stages)
            {
                if (stage.ArchitecturePayload is not Ltx2StagePayload payload)
                {
                    continue;
                }
                if (payload.Retake is not null && !payload.FrameReferences.IsDefaultOrEmpty)
                {
                    diagnostics.Add(Error(
                        RetakeAndReferencesAreExclusive,
                        clip.ClipId,
                        stage.StageId));
                }
                if (payload.Retake is not null
                    && !clip.IsSourced
                    && root.HostKind != HostRootKind.GlobalRefineSource)
                {
                    diagnostics.Add(Error(RetakeRequiresSource, clip.ClipId, stage.StageId));
                }
            }
        }

        if (timelineClips.Count > 1)
        {
            bool[] hdrClips = [.. timelineClips.Select(clip =>
                clip.Architecture.Id == Ltx2ArchitectureModule.ArchitectureId
                && HdrIcLoraPolicy.IsActive(clip))];
            if (hdrClips.Any(value => value) && hdrClips.Any(value => !value))
            {
                diagnostics.Add(Error(HdrRequiresUniformTimeline));
            }
        }
        return diagnostics.AsReadOnly();
    }

    private static VideoPlanDiagnostic Error(
        RuleDecision rule,
        int? clipId = null,
        int? stageId = null) =>
        Diagnostic(rule, VideoPlanDiagnosticSeverity.Error, clipId, stageId);

    private static VideoPlanDiagnostic Diagnostic(
        RuleDecision rule,
        VideoPlanDiagnosticSeverity severity,
        int? clipId = null,
        int? stageId = null) =>
        new(
            severity,
            rule.Code,
            rule.Reason,
            clipId,
            stageId);
}
