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
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ArchitectureClipCompileContext context)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stageModels);
        ArgumentNullException.ThrowIfNull(context);
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
        Refuse(
            context.EntryMode == ArchitectureEntryMode.RefineVideo,
            "request-global refine-video entry");
        string clipCompatibilityClassId = null;
        foreach ((int rawStageIndex, ResolvedVideoModel resolved) in stageModels
            .OrderBy(pair => pair.Key))
        {
            if (resolved is null
                || resolved.ArchitectureId != WanArchitectureModule.ArchitectureId)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(resolved.ModelClassId)
                || string.IsNullOrWhiteSpace(resolved.CompatibilityClassId)
                || resolved.EntryAbilities == VideoModelEntryAbility.None)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "wan.model-facts.missing",
                    $"Clip {clip.Id} authored stage {rawStageIndex} is missing the host model "
                        + "class, compatibility class, or entry abilities required to plan WAN.",
                    clip.Id,
                    RawStageIndex: rawStageIndex));
                continue;
            }
            if (clipCompatibilityClassId is null)
            {
                clipCompatibilityClassId = resolved.CompatibilityClassId;
            }
            else if (!string.Equals(
                resolved.CompatibilityClassId,
                clipCompatibilityClassId,
                StringComparison.Ordinal))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "wan.clip-compatibility-class.mixed",
                    $"Clip {clip.Id} must use one host model compatibility class throughout, "
                        + $"but authored stage {rawStageIndex} resolves to "
                        + $"'{resolved.CompatibilityClassId}' after "
                        + $"'{clipCompatibilityClassId}'. Use a separate clip to change model "
                        + "compatibility families.",
                    clip.Id,
                    StageId: null,
                    RawStageIndex: rawStageIndex));
            }
        }
        for (int stageIndex = 0; stageIndex < authoredStages.Count; stageIndex++)
        {
            StageSpec stage = authoredStages[stageIndex];
            bool resolvedStage = stageModels.TryGetValue(
                    stage.ClipStageRawIndex,
                    out ResolvedVideoModel resolved);
            bool supportedModel =
                resolvedStage
                && resolved is not null
                && resolved.ArchitectureId == WanArchitectureModule.ArchitectureId
                && !string.IsNullOrWhiteSpace(resolved.ModelClassId)
                && !string.IsNullOrWhiteSpace(resolved.CompatibilityClassId)
                && resolved.EntryAbilities != VideoModelEntryAbility.None;
            if (!supportedModel)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "wan22.stage-model.unsupported",
                    $"Clip {clip.Id} stage {stage.Id} must resolve to architecture "
                        + $"'{WanArchitectureModule.ArchitectureId}' with usable host model "
                        + "facts, but resolved "
                        + $"architecture '{resolved?.ArchitectureId.ToString() ?? "<missing>"}' "
                        + $"model class '{resolved?.ModelClassId ?? "<missing>"}'.",
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
                    // The common text-root parser canonicalizes every authored guide selector to
                    // Generated. Common ClipPlanCompiler still owns the executable chain and
                    // deliberately assigns PreviousStage to every later stage.
                    && context.EntryMode != ArchitectureEntryMode.TextToVideo
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
            WanStagePayload payload = new(
                    resolved?.ModelName ?? stage.Model,
                    stage.Control,
                    stage.Steps,
                    stage.CfgScale,
                    stage.Sampler,
                    stage.Scheduler,
                    StageUpscalePlanCompiler.Compile(stage),
                    loras)
            {
                ModelClassId = resolved?.ModelClassId ?? "",
                CompatibilityClassId = resolved?.CompatibilityClassId ?? "",
            };
            stages.Add(stage.ClipStageRawIndex, payload);
        }
        WanFrameReferencePlan firstReference = CompileReference(
            (clip.ImageRefs ?? []).SingleOrDefault(reference =>
                !reference.FromEnd && reference.Frame == 1));
        WanFrameReferencePlan lastReference = CompileReference(
            (clip.ImageRefs ?? []).SingleOrDefault(reference =>
                reference.FromEnd && reference.Frame == 1));
        return new(
            new WanClipPayload(
                clip.Id,
                firstReference,
                lastReference)
            {
                CompatibilityClassId = clipCompatibilityClassId ?? "",
            },
            stages,
            diagnostics.AsReadOnly());
    }

    private static WanFrameReferencePlan CompileReference(ImageRefSpec reference) =>
        reference is null
            ? null
            : new(
                reference.Source?.Trim() ?? "",
                reference.UploadFileName,
                reference.Data);
}
