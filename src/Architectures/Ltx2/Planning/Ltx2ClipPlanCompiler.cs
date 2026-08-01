using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Compiles all LTX-owned settings before the common clip plan is assembled.</summary>
internal sealed record Ltx2ClipPlanCompilation(
    Ltx2ClipPayload Payload,
    IReadOnlyDictionary<int, Ltx2StagePayload> Stages,
    IReadOnlyList<PlanDiagnostic> Diagnostics);

internal static class Ltx2ClipPlanCompiler
{
    internal static Ltx2ClipPlanCompilation Compile(
        ClipSpec clip,
        ArchitectureClipCompileContext context)
    {
        IcLoraClipPlanCompilation icLoras =
            IcLoraPlanCompiler.CompileClip(clip, context);
        Ltx2AudioPlan audio = Ltx2AudioPlanCompiler.Compile(
            clip,
            icLoras.PrimaryControlNetSourceIndex);
        PromptRelayPlan relay = PromptRelayPlanCompiler.Compile(
            clip,
            context.FramesPerSecond);
        List<PlanDiagnostic> diagnostics = [
            .. audio.Diagnostics.Select(diagnostic => diagnostic with { ClipId = clip.Id }),
            .. icLoras.Diagnostics,
        ];
        Dictionary<int, Ltx2StagePayload> stages = [];
        foreach (StageSpec stage in clip.Stages ?? [])
        {
            RetakePlan retake = CompileRetake(stage.RetakeWindow);
            if (retake is not null && context.EntryMode != ArchitectureEntryMode.InitVideo)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "retake-source-required",
                    "Retake was ignored because the clip has no init video.",
                    clip.Id,
                    stage.Id));
                retake = null;
            }
            Ltx2StagePayload payload = new(
                new StageCorePlan(
                    stage.Control,
                    stage.Steps,
                    stage.CfgScale,
                    stage.Sampler,
                    stage.Scheduler,
                    StageUpscalePlanCompiler.Compile(stage),
                    NormalLoraPlanCompiler.Compile(clip, stage)),
                CompileGuideReference(stage.ImageReference),
                stage.ImageRefWasExplicit,
                icLoras.Stages[stage.ClipStageRawIndex],
                retake,
                relay,
                ImageReferencePlanCompiler.Compile(clip, stage),
                CompileAudioAction(audio, stage));
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
                audio.Reuse,
                audio.Injection,
                icLoras.PrimaryControlNetSourceIndex,
                clip.ReferenceFraming),
            stages,
            diagnostics.AsReadOnly());
    }

    private static RetakePlan CompileRetake(RetakeWindowSpec retake) => retake is null
        ? null
        : new(
            retake.StartFrame,
            retake.LengthFrames,
            retake.Strength);

    private static GuideReferencePlan CompileGuideReference(string rawValue)
    {
        string raw = rawValue?.Trim() ?? "";
        StageGuideReferenceSelection selection = StageGuideReferencePolicy.Classify(raw);
        return new(selection.Kind, raw, selection.ReferencedStageIndex);
    }

    private static StageAudioAction CompileAudioAction(Ltx2AudioPlan audio, StageSpec stage)
    {
        if (!audio.Reuse.IsEligible)
        {
            return StageAudioAction.None;
        }
        if (stage.ClipStageIndex == audio.Reuse.CaptureStageIndex)
        {
            return StageAudioAction.CaptureForReuse;
        }
        return stage.ClipStageIndex >= audio.Reuse.ReuseFromStageIndex
            ? StageAudioAction.ReuseCaptured
            : StageAudioAction.None;
    }
}
