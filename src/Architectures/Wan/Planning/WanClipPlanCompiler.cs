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

        Dictionary<int, StockHostVideoStagePayload> stages = [];
        IReadOnlyList<StageSpec> activeStages = clip.Stages ?? [];
        bool initVideoEntry = clip.InitVideo is not null;
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
        (WanFrameReferencePlan firstReference, WanFrameReferencePlan lastReference) =
            CompileFrameReferences(clip, activeStages, stageModels, diagnostics);
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

    /// <summary>
    /// Wan native conditioning accepts one uploaded first frame and one uploaded final frame, and
    /// only where the governing stage model declares that reference position. Every other authored
    /// reference stays saved and is reported as dropped for this generation.
    /// </summary>
    private static (WanFrameReferencePlan First, WanFrameReferencePlan Last) CompileFrameReferences(
        ClipSpec clip,
        IReadOnlyList<StageSpec> activeStages,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ICollection<PlanDiagnostic> diagnostics)
    {
        WanFrameReferencePlan first = null;
        WanFrameReferencePlan last = null;
        void Ignore(string code, string message) =>
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                code,
                message,
                clip.Id));
        bool Declares(StageSpec stage, string position, out string modelName)
        {
            ResolvedVideoModel model = stage is null
                ? null
                : stageModels.GetValueOrDefault(stage.ClipStageRawIndex);
            modelName = model?.ModelName ?? "<missing>";
            return model?.ReferencePositions?.Contains(position, StringComparer.Ordinal) == true;
        }

        foreach (ImageRefSpec reference in clip.ImageRefs ?? [])
        {
            bool isFirst = !reference.FromEnd && reference.Frame == 1;
            bool isLast = reference.FromEnd && reference.Frame == 1;
            if (!isFirst && !isLast)
            {
                Ignore(
                    "effective-request.wan-middle-frame-reference-ignored",
                    $"Clip {clip.Id} has a WAN image reference at "
                        + $"{(reference.FromEnd ? "end-relative" : "start-relative")} frame "
                        + $"{reference.Frame}. WAN native conditioning accepts only the first "
                        + "and final frame. The authored reference remains saved and is ignored "
                        + "for this generation.");
                continue;
            }
            if (isFirst && clip.InitVideo is not null)
            {
                Ignore(
                    "effective-request.wan-init-video-first-frame-reference-ignored",
                    $"Clip {clip.Id} already enters from init-video video, so its separate "
                        + "first-frame reference remains saved and is ignored for this "
                        + "generation.");
                continue;
            }
            if (isFirst
                && !Declares(activeStages.FirstOrDefault(), "first", out string firstModel))
            {
                Ignore(
                    "effective-request.wan-first-frame-reference-ignored",
                    $"Clip {clip.Id}'s first-frame reference is not supported by the selected "
                        + $"first WAN model '{firstModel}'. The authored reference remains saved "
                        + "and is ignored for this generation.");
                continue;
            }
            if (!StringUtils.Equals(reference.Source, "Upload"))
            {
                Ignore(
                    "effective-request.wan-frame-reference-source-ignored",
                    $"Clip {clip.Id}'s WAN {(isFirst ? "first" : "final")}-frame reference uses "
                        + $"source '{reference.Source}', but the native WAN bounded-reference "
                        + "path currently accepts uploaded images. The authored reference "
                        + "remains saved and is ignored for this generation.");
                continue;
            }
            if (isFirst ? first is not null : last is not null)
            {
                Ignore(
                    "effective-request.wan-duplicate-frame-reference-ignored",
                    $"Clip {clip.Id} has more than one WAN {(isFirst ? "first" : "last")} frame "
                        + "reference. The first authored reference remains active; later "
                        + "references remain saved and are ignored for this generation.");
                continue;
            }
            if (isLast
                && !Declares(
                    activeStages.LastOrDefault(stage => !stage.IsPassthrough),
                    "last",
                    out string terminalModel))
            {
                Ignore(
                    "effective-request.wan-last-frame-reference-ignored",
                    $"Clip {clip.Id}'s final-frame reference is not supported by the selected "
                        + $"terminal WAN model '{terminalModel}'. The authored reference remains "
                        + "saved and is ignored for this generation.");
                continue;
            }
            if (isFirst)
            {
                first = CompileReference(reference);
            }
            else
            {
                last = CompileReference(reference);
            }
        }
        return (first, last);
    }

    private static WanFrameReferencePlan CompileReference(ImageRefSpec reference) =>
        reference is null
            ? null
            : new(
                reference.Source?.Trim() ?? "",
                reference.UploadFileName,
                reference.Data);
}
