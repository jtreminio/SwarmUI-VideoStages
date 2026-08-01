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
/// Compiles Wan-owned clip settings and reports unsupported or normalized options.
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
        void WarnAndNormalize(bool configured, string option, int? stageId = null)
        {
            if (configured)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "wan22.option.unsupported",
                    $"Clip {clip.Id} configures '{option}', which architecture "
                        + $"'{WanArchitectureModule.ArchitectureId}' normalizes to 'PreviousStage'.",
                    clip.Id,
                    stageId));
            }
        }

        Dictionary<int, StockHostVideoStagePayload> stages = [];
        IReadOnlyList<StageSpec> activeStages = clip.Stages ?? [];
        bool initVideoEntry = clip.InitVideo is not null;
        // Resolved stage models are a prerequisite; indexing asserts that planning contract.
        string clipCompatibilityClassId = activeStages.Count == 0
            ? ""
            : stageModels[activeStages[0].ClipStageRawIndex].CompatibilityClassId;
        for (int stageIndex = 0; stageIndex < activeStages.Count; stageIndex++)
        {
            StageSpec stage = activeStages[stageIndex];
            ResolvedVideoModel resolved = stageModels[stage.ClipStageRawIndex];
            bool firstStage = stageIndex == 0;
            bool decodedStageInput = initVideoEntry || !firstStage;
            ImmutableArray<NormalLoraPlan> loras =
                NormalLoraPlanCompiler.Compile(
                    clip,
                    stage,
                    NormalLoraTargetPolicy.ModelOnly);
            WarnAndNormalize(
                !firstStage
                    // Text-root parsing canonicalizes selectors to Generated. Other later stages
                    // execute from PreviousStage.
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
                    resolved.ModelClassId,
                    resolved.CompatibilityClassId,
                    NormalLoraTargetPolicy.ModelOnly,
                    new StageCorePlan(
                        stage.Control,
                        stage.Steps,
                        stage.CfgScale,
                        stage.Sampler,
                        stage.Scheduler,
                        StageUpscalePlanCompiler.Compile(stage),
                        loras));
            stages.Add(stage.ClipStageRawIndex, payload);
        }
        WanFrameReferencePlan firstReference = CompileReference(
            (clip.ImageRefs ?? []).FirstOrDefault(reference =>
                !reference.FromEnd && reference.Frame == 1));
        WanFrameReferencePlan lastReference = CompileReference(
            (clip.ImageRefs ?? []).FirstOrDefault(reference =>
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
