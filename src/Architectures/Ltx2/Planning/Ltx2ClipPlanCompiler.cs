using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Compiles all LTX-owned settings before the common clip plan is assembled.</summary>
internal sealed record Ltx2ClipPlanCompilation(
    Ltx2ClipPayload Payload,
    IReadOnlyList<PlanDiagnostic> Diagnostics);

internal static class Ltx2ClipPlanCompiler
{
    internal static Ltx2ClipPlanCompilation Compile(
        ClipSpec clip,
        ArchitectureClipCompileContext context)
    {
        Ltx2AudioPlan audio = Ltx2AudioPlanCompiler.Compile(clip);
        PromptRelayPlan relay = PromptRelayPlanCompiler.Compile(
            clip,
            context.FramesPerSecond);
        List<PlanDiagnostic> diagnostics = [
            .. audio.Diagnostics.Select(diagnostic => diagnostic with { ClipId = clip.Id }),
            .. IcLoraPlanCompiler.ValidateClip(clip, context),
        ];
        Dictionary<int, Ltx2StagePayload> stages = [];
        foreach (StageSpec stage in clip.Stages ?? [])
        {
            Ltx2StagePayload payload = new(
                new StageCorePlan(
                    stage.Model,
                    stage.Control,
                    stage.Steps,
                    stage.CfgScale,
                    stage.Sampler,
                    stage.Scheduler,
                    stage.ControlNetStrength,
                    stage.ImageRefWasExplicit),
                GuideReferencePlanCompiler.Compile(stage.ImageReference),
                CompileUpscale(stage),
                NormalLoraPlanCompiler.Compile(clip, stage),
                IcLoraPlanCompiler.Compile(clip, stage, context),
                CompileRetake(stage.RetakeWindow),
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
                clip.Id,
                stages,
                audio.Reuse,
                audio.Injection,
                audio.ControlNetSourceIndex,
                clip.ReferenceFraming),
            diagnostics.AsReadOnly());
    }

    private static StageUpscalePlan CompileUpscale(StageSpec stage)
    {
        StageUpscaleMode mode;
        if (stage.Upscale == 1)
        {
            mode = StageUpscaleMode.None;
        }
        else if (stage.IsPixelUpscale)
        {
            mode = StageUpscaleMode.Pixel;
        }
        else if (stage.IsModelUpscale)
        {
            mode = StageUpscaleMode.Model;
        }
        else if (stage.IsLatentUpscale)
        {
            mode = StageUpscaleMode.Latent;
        }
        else if (stage.IsLatentModelUpscale)
        {
            mode = StageUpscaleMode.LatentModel;
        }
        else
        {
            mode = StageUpscaleMode.Unsupported;
        }

        string raw = stage.UpscaleMethod?.Trim() ?? "";
        int separator = raw.IndexOf('-');
        string methodName = separator >= 0 && separator < raw.Length - 1
            ? raw[(separator + 1)..]
            : raw;
        return new(mode, stage.Upscale, raw, methodName);
    }

    private static RetakePlan CompileRetake(RetakeWindowSpec retake) => retake is null
        ? null
        : new(
            retake.StartFrame,
            retake.LengthFrames,
            retake.Strength);

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
