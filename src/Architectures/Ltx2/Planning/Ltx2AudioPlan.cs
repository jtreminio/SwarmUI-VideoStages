using System.Collections.Immutable;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

internal sealed record AudioReusePlan(
    bool IsRequested,
    bool IsEligible,
    int CaptureStageIndex,
    int ReuseFromStageIndex);

internal sealed record Ltx2AudioPlan(
    AudioReusePlan Reuse,
    int? ControlNetSourceIndex,
    ImmutableArray<AudioPlanDiagnostic> Diagnostics);

internal static class Ltx2AudioPlanCompiler
{
    internal static Ltx2AudioPlan Compile(ClipSpec clip)
    {
        AudioPlanComponentResult<AudioReusePlan> reuse =
            AudioReusePlanCompiler.Compile(clip);
        int? controlNetSourceIndex =
            IcLoraPlanCompiler.ResolvePrimaryControlNetSourceIndex(clip);
        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<AudioPlanDiagnostic>();
        diagnostics.AddRange(reuse.Diagnostics);
        if (clip.ClipLengthFromControlNet && controlNetSourceIndex is null)
        {
            diagnostics.Add(new(
                "audio.length.controlnet_owner_has_no_source",
                "ControlNet owns clip length, but no valid LTX ControlNet 1-3 drive source is configured."));
        }
        return new(
            reuse.Plan,
            controlNetSourceIndex,
            diagnostics.ToImmutable());
    }
}
