using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

/// <summary>
/// Compiles Wan-owned clip settings and reports unsupported or normalized options.
/// </summary>
internal static class WanClipPlanCompiler
{
    internal static ArchitectureClipCompilation Compile(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ArchitectureClipCompileContext context)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stageModels);
        ArgumentNullException.ThrowIfNull(context);
        static bool HasNoiseRole(string modelName, string role)
        {
            string normalized = string.Concat(
                    (modelName ?? "").Where(char.IsLetterOrDigit))
                .ToLowerInvariant();
            return normalized.Contains($"{role}noise", StringComparison.Ordinal);
        }
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

        Dictionary<int, IArchitectureStagePayload> stages = [];
        IReadOnlyList<StageSpec> activeStages = clip.Stages ?? [];
        bool initVideoEntry = context.EntryMode == ArchitectureEntryMode.InitVideo;
        bool previousStageContinuesSampling = false;
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
            StageSpec previousStage = firstStage ? null : activeStages[stageIndex - 1];
            ResolvedVideoModel previousModel = firstStage
                ? null
                : stageModels[previousStage.ClipStageRawIndex];
            int continuationStartStep =
                HostVideoStageSchedulePolicy.StartStep(stage.Steps, stage.Control);
            bool continuesPreviousSampling =
                context.EntryMode != ArchitectureEntryMode.TextToVideo
                && !previousStageContinuesSampling
                && previousStage is not null
                && previousStage.Control == 1
                && continuationStartStep > 0
                && continuationStartStep < stage.Steps
                && stage.Upscale == 1
                && previousStage.Steps == stage.Steps
                && string.Equals(
                    previousStage.Scheduler,
                    stage.Scheduler,
                    StringComparison.OrdinalIgnoreCase)
                && HasNoiseRole(previousModel.ModelName, "high")
                && HasNoiseRole(resolved.ModelName, "low")
                && string.Equals(
                    previousModel.ModelClassId,
                    WanArchitectureModule.ImageToVideoModelClassId,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    resolved.ModelClassId,
                    WanArchitectureModule.ImageToVideoModelClassId,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    previousModel.CompatibilityClassId,
                    resolved.CompatibilityClassId,
                    StringComparison.Ordinal);
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
                        loras))
                {
                    ContinuesSamplingFromPreviousStage = continuesPreviousSampling,
                };
            stages.Add(stage.ClipStageRawIndex, payload);
            previousStageContinuesSampling = continuesPreviousSampling;
        }
        WarnAboutReferenceStrengths(clip, activeStages, diagnostics);
        (NativeFrameReferencePlan firstReference, NativeFrameReferencePlan lastReference) =
            NativeFrameReferences.Compile(
                clip,
                activeStages,
                stageModels,
                WanArchitectureModule.Instance.Descriptor,
                diagnostics,
                "wan",
                "WAN");
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

    /// <summary>
    /// Wan native conditioning has no per-reference strength input, so authored strengths cannot
    /// reach the graph. They stay saved and are reported once per clip rather than dropped silently.
    /// </summary>
    private static void WarnAboutReferenceStrengths(
        ClipSpec clip,
        IReadOnlyList<StageSpec> activeStages,
        ICollection<PlanDiagnostic> diagnostics)
    {
        bool custom = activeStages.Any(stage =>
            stage.ImageRefStrengths?.Any(strength =>
                Math.Abs(strength - Constants.DefaultStageRefStrength) > 0.000001) == true);
        if (custom)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.wan-reference-strengths-ignored",
                $"Clip {clip.Id} configures per-stage frame-reference strengths, which WAN "
                    + "native conditioning does not use. The authored strengths remain saved "
                    + "and are ignored for this generation.",
                clip.Id));
        }
    }

}
