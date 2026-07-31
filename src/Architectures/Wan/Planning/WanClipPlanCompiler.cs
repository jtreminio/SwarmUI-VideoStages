using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

internal sealed record WanClipPlanCompilation(
    WanClipPayload Payload,
    IReadOnlyDictionary<int, StockHostVideoStagePayload> Stages,
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

        Dictionary<int, StockHostVideoStagePayload> stages = [];
        IReadOnlyList<StageSpec> activeStages = clip.Stages ?? [];
        bool sourcedEntry = clip.SourceVideo is not null;
        Refuse(
            context.EntryMode == ArchitectureEntryMode.RefineVideo,
            "request-global refine-video entry");
        // The registry owns model-fact validity, the resolver owns same-architecture and
        // same-compatibility admission, and the common capability validator owns stage entry roles.
        // Reaching this compiler means those contracts passed; the indexer is intentionally an
        // invariant assertion rather than a fourth user-facing model validation layer.
        string clipCompatibilityClassId = activeStages.Count == 0
            ? ""
            : stageModels[activeStages[0].ClipStageRawIndex].CompatibilityClassId;
        for (int stageIndex = 0; stageIndex < activeStages.Count; stageIndex++)
        {
            StageSpec stage = activeStages[stageIndex];
            ResolvedVideoModel resolved = stageModels[stage.ClipStageRawIndex];
            bool firstStage = stageIndex == 0;
            bool decodedStageInput = sourcedEntry || !firstStage;
            ImmutableArray<NormalLoraPlan> loras =
                NormalLoraPlanCompiler.Compile(
                    clip,
                    stage,
                    NormalLoraTargetPolicy.ModelOnly);
            if (decodedStageInput
                && stage.Control <= HostVideoStageRules
                    .NormalLoraRequiresSamplingStage
                    .Require<MinimumStageControlRuleConstraints>()
                    .ExclusiveMinimumControl
                && !loras.IsDefaultOrEmpty)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    HostVideoStageRules.NormalLoraRequiresSamplingStageCode,
                    HostVideoStageRules.NormalLoraRequiresSamplingStageReason,
                    clip.Id,
                    stage.Id));
            }
            Refuse(
                !firstStage
                    // Text-root parsing canonicalizes every selector to Generated. In other entry
                    // modes, Generated is individually supported but cannot describe the executable
                    // later-stage edge, which common compilation always wires from PreviousStage.
                    && context.EntryMode != ArchitectureEntryMode.TextToVideo
                    && !StringUtils.Equals(stage.ImageReference, "PreviousStage"),
                "a later-stage input other than 'PreviousStage'",
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
                    && HostVideoStageSchedulePolicy.IsQuantizedZeroPartial(
                        stage.Steps,
                        stage.Control),
                "decoded-input partial control that quantizes to sampler start step 0",
                stage.Id);
            StockHostVideoStagePayload payload = new(
                    WanArchitectureModule.ArchitectureId,
                    resolved.ModelName,
                    resolved.ModelClassId,
                    resolved.CompatibilityClassId,
                    NormalLoraTargetPolicy.ModelOnly,
                    stage.Control,
                    stage.Steps,
                    stage.CfgScale,
                    stage.Sampler,
                    stage.Scheduler,
                    StageUpscalePlanCompiler.Compile(stage),
                    loras);
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
                CompatibilityClassId = clipCompatibilityClassId,
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
