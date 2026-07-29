using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

internal sealed record WanClipPlanCompilation(
    WanClipPayload Payload,
    IReadOnlyDictionary<int, WanStagePayload> Stages,
    IReadOnlyList<PlanDiagnostic> Diagnostics);

/// <summary>
/// Compiles the Wan-owned settings of one clip before the common clip plan is assembled. Settings
/// this slice cannot honor are refused here rather than dropped: a compiled payload is the whole
/// instruction, so anything it omits must have been rejected.
/// </summary>
internal static class WanClipPlanCompiler
{
    internal static WanClipPlanCompilation Compile(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stageModels);
        List<PlanDiagnostic> diagnostics = [];
        void Refuse(bool configured, string option, int? stageId = null)
        {
            if (configured)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "wan22.option.unsupported",
                    $"Clip {clip.Id} configures '{option}', which architecture "
                        + $"'{WanArchitectureModule.ArchitectureId}' does not support.",
                    clip.Id,
                    stageId));
            }
        }

        Dictionary<int, WanStagePayload> stages = [];
        IReadOnlyList<StageSpec> authoredStages = clip.Stages ?? [];
        bool sourcedEntry = clip.SourceVideo is not null;
        ModelProfileId? clipProfile = null;
        for (int stageIndex = 0; stageIndex < authoredStages.Count; stageIndex++)
        {
            StageSpec stage = authoredStages[stageIndex];
            bool resolvedStage = stageModels.TryGetValue(
                    stage.ClipStageRawIndex,
                    out ResolvedVideoModel resolved);
            bool supportedProfile =
                resolvedStage
                && resolved is not null
                && resolved.ArchitectureId == WanArchitectureModule.ArchitectureId
                && WanArchitectureModule.IsSupportedProfile(resolved.ModelProfileId);
            if (!supportedProfile)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "wan22.stage-profile.unsupported",
                    $"Clip {clip.Id} stage {stage.Id} must resolve to architecture "
                        + $"'{WanArchitectureModule.ArchitectureId}' and one of its supported "
                        + $"profiles "
                        + $"'{string.Join(
                            "', '",
                            WanArchitectureModule.Instance.Descriptor.Profiles.Select(
                                profile => profile.Id))}', but resolved "
                        + $"architecture '{resolved?.ArchitectureId.ToString() ?? "<missing>"}' "
                        + $"profile '{resolved?.ModelProfileId.ToString() ?? "<missing>"}'.",
                    clip.Id,
                    stage.Id));
            }
            else if (clipProfile is null)
            {
                clipProfile = resolved.ModelProfileId;
            }
            else if (resolved.ModelProfileId != clipProfile.Value)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "wan22.clip-profile.mixed",
                    $"Clip {clip.Id} must use one Wan profile throughout, but stage {stage.Id} "
                        + $"resolves to '{resolved.ModelProfileId}' after "
                        + $"'{clipProfile.Value}'. Use a hard-cut clip to change profiles.",
                    clip.Id,
                    stage.Id));
            }
            bool firstStage = stageIndex == 0;
            bool decodedStageInput = sourcedEntry || !firstStage;
            ImmutableArray<NormalLoraPlan> loras =
                NormalLoraPlanCompiler.Compile(
                    clip,
                    stage,
                    NormalLoraTargetPolicy.ModelOnly);
            Refuse(
                stage.IsPassthrough && !decodedStageInput,
                "a generated-root stage that generates nothing",
                stage.Id);
            if (decodedStageInput
                && stage.Control <= WanArchitectureModule
                    .NormalLoraRequiresSamplingStageRule
                    .Require<MinimumStageControlRuleConstraints>()
                    .ExclusiveMinimumControl
                && !loras.IsDefaultOrEmpty)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    WanArchitectureModule.NormalLoraRequiresSamplingStageCode,
                    WanArchitectureModule.NormalLoraRequiresSamplingStageReason,
                    clip.Id,
                    stage.Id));
            }
            Refuse(
                firstStage
                    && !StringUtils.Equals(stage.ImageReference, "Generated"),
                "a first-stage input other than 'Generated'",
                stage.Id);
            Refuse(
                !firstStage
                    && !StringUtils.Equals(stage.ImageReference, "PreviousStage"),
                "a later-stage input other than 'PreviousStage'",
                stage.Id);
            Refuse(
                firstStage
                    && !sourcedEntry
                    && (!double.IsFinite(stage.Control) || stage.Control != 1),
                "generated first-stage control other than full generation (1)",
                stage.Id);
            Refuse(
                decodedStageInput
                    && (!double.IsFinite(stage.Control)
                        || stage.Control < 0
                        || stage.Control > 1),
                "decoded-input control outside the finite range [0, 1]",
                stage.Id);
            Refuse(
                decodedStageInput
                    && double.IsFinite(stage.Control)
                    && stage.Control > 0
                    && WanStageSchedulePolicy.IsQuantizedZeroPartial(
                        stage.Steps,
                        stage.Control),
                "decoded-input partial control that quantizes to sampler start step 0",
                stage.Id);
            stages.Add(
                stage.ClipStageRawIndex,
                new WanStagePayload(
                    resolved?.ModelName ?? stage.Model,
                    resolved?.ModelProfileId
                        ?? WanArchitectureModule.ImageToVideoProfileId,
                    stage.Control,
                    stage.Steps,
                    stage.CfgScale,
                    stage.Sampler,
                    stage.Scheduler,
                    loras));
        }
        return new(
            new WanClipPayload(
                clip.Id,
                clipProfile ?? WanArchitectureModule.ImageToVideoProfileId),
            stages,
            diagnostics.AsReadOnly());
    }
}
