using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Planning;
using VideoStages.Architectures.Ltx2.Runtime.Stage;
using VideoStages.CharacterRefine;

namespace VideoStages.Architectures.Ltx2.Planning;

internal static class Ltx2ClipPlanCompiler
{
    internal static ArchitectureClipCompilation Compile(
        ClipSpec clip,
        ArchitectureClipCompileContext context)
    {
        IcLoraClipPlanCompilation icLoras =
            IcLoraPlanCompiler.CompileClip(clip, context);
        (Ltx2AudioInjectionPlan audioInjection, ImmutableArray<PlanDiagnostic> audioDiagnostics) =
            Ltx2AudioInjectionPlanCompiler.Compile(
                clip,
                icLoras.PrimaryControlNetSourceIndex);
        PromptRelayPlan relay = PromptRelayPlanCompiler.Compile(
            clip,
            context.FramesPerSecond);
        List<PlanDiagnostic> diagnostics = [
            .. audioDiagnostics.Select(diagnostic => diagnostic with { ClipId = clip.Id }),
            .. icLoras.Diagnostics,
        ];
        Dictionary<int, IArchitectureStagePayload> stages = [];
        foreach (StageSpec stage in clip.Stages ?? [])
        {
            bool passthrough = StagePassthroughPolicy.IsPassthrough(
                stage,
                Ltx2ArchitectureModule.Instance.Descriptor);
            bool characterRefine = stage.CharacterRefine is not null && !passthrough;
            bool decodedInput = !passthrough
                && StagePassthroughPolicy.HasDecodedVideoBefore(
                    clip,
                    stage,
                    context.EntryMode,
                    Ltx2ArchitectureModule.Instance.Descriptor);
            RetakePlan retake = CompileRetake(stage.RetakeWindow);
            if (retake is not null && context.EntryMode != ArchitectureEntryMode.InitVideo)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    PlanDiagnosticCodes.RetakeSourceRequired,
                    "Retake was ignored because the clip has no init video.",
                    clip.Id,
                    stage.Id));
                retake = null;
            }
            if (characterRefine && !decodedInput)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "ltx2.character-refine.decoded-input-required",
                    $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita requires InitVideo or "
                        + "the previous stage's decoded output.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            StageUpscalePlan upscale = StageUpscalePlanCompiler.Compile(stage);
            if (characterRefine && upscale.Mode != StageUpscaleMode.None)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "ltx2.character-refine.upscale-unsupported",
                    $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita cannot upscale while "
                        + "tracking. Use a separate stage to upscale first.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            if (characterRefine && retake is not null)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "ltx2.character-refine.retake-unsupported",
                    $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita and Retake cannot run "
                        + "in the same stage.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            ImmutableArray<IcLoraPlan> stageIcLoras =
                icLoras.Stages[stage.ClipStageRawIndex];
            if (characterRefine && !stageIcLoras.IsDefaultOrEmpty)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "ltx2.character-refine.ic-lora-unsupported",
                    $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita and IC-LoRAs cannot "
                        + "run in the same stage.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            ImmutableArray<FrameRefPlan> frameReferences =
                stage.CharacterRefine is not null
                    ? []
                    : FrameRefPlanCompiler.Compile(clip, stage);
            if (characterRefine && stage.ImageRefWasExplicit)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "ltx2.character-refine.guide-unsupported",
                    $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita cannot use a stage "
                        + "image guide. Remove the guide or use it in a separate stage.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            if (characterRefine
                && (AudioSourceKindPolicy.CanUseAudioDerivedLength(clip)
                    || clip.ClipLengthFromControlNet))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "ltx2.character-refine.runtime-duration-unsupported",
                    $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita requires a fixed clip "
                        + "duration; audio- and ControlNet-derived durations are not supported.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            Ltx2StagePayload payload = new(
                new StageCorePlan(
                    stage.Control,
                    stage.Steps,
                    stage.CfgScale,
                    stage.Sampler,
                    stage.Scheduler,
                    upscale,
                    LoraPlanCompiler.Compile(stage, LoraTarget.ModelAndTextEncoder)),
                CompileGuideReference(stage.ImageReference),
                stage.ImageRefWasExplicit,
                stageIcLoras,
                retake,
                relay,
                frameReferences,
                CompileAudioAction(clip, stage),
                characterRefine
                    ? CompileCharacterRefine(clip, stage, diagnostics)
                    : null);
            stages.Add(stage.ClipStageRawIndex, payload);
            PlanDiagnostic dimensionDiagnostic = StageDimensionRules.SnapDiagnostic(
                clip.Id,
                stage.Id,
                payload.IcLoras,
                context.Width,
                context.Height);
            if (dimensionDiagnostic is not null)
            {
                diagnostics.Add(dimensionDiagnostic);
            }
        }
        return new(
            new Ltx2ClipPayload(
                audioInjection,
                icLoras.PrimaryControlNetSourceIndex,
                clip.ReferenceFraming),
            stages,
            diagnostics.AsReadOnly());
    }

    private static LtxCharacterRefinePlan CompileCharacterRefine(
        ClipSpec clip,
        StageSpec stage,
        ICollection<PlanDiagnostic> diagnostics)
    {
        CharacterRefineSpec settings = stage.CharacterRefine;
        List<LtxCharacterRefineReferencePlan> identities = [];
        if (settings.VerifyIdentity)
        {
            identities.AddRange((clip.References ?? [])
                .Where(candidate =>
                    candidate.Kind == ClipReferenceKind.Image
                    && CharacterRefineCanRead(candidate))
                .Select(reference => new LtxCharacterRefineReferencePlan(
                    reference.Source,
                    reference.Media)));
            if (identities.Count == 0)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "ltx2.character-refine.identity-reference-missing",
                    $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita Verify Identity "
                        + "requires a usable image reference on this clip.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
        }
        return new(
            CharacterRefineSettingsPlan.From(settings),
            identities.AsReadOnly());
    }

    private static bool CharacterRefineCanRead(ClipReferenceSpec reference) =>
        StringUtils.Equals(reference.Source, MediaSource.Upload)
            ? !string.IsNullOrWhiteSpace(reference.Media?.Data)
            : MediaSource.IsHostStageSource(reference.Source)
                || MediaSource.TryParseControlNetIndex(reference.Source, out _);

    private static RetakePlan CompileRetake(RetakeWindowSpec retake) => retake is null
        ? null
        : new(
            retake.StartFrame,
            retake.LengthFrames,
            retake.Strength);

    private static GuideReferencePlan CompileGuideReference(string rawValue)
    {
        StageGuideReferenceSelection selection = StageGuideReference.Classify(rawValue);
        return new(selection.Kind, rawValue, selection.ReferencedStageIndex);
    }

    private static StageAudioAction CompileAudioAction(ClipSpec clip, StageSpec stage)
    {
        if (!clip.ReuseAudio || (clip.Stages?.Count ?? 0) < 3)
        {
            return StageAudioAction.None;
        }
        if (stage.ClipStageIndex == 1)
        {
            return StageAudioAction.CaptureForReuse;
        }
        return stage.ClipStageIndex >= 2
            ? StageAudioAction.ReuseCaptured
            : StageAudioAction.None;
    }
}
