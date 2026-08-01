using System.Collections.Immutable;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

internal sealed record AudioReusePlan(
    bool IsRequested,
    bool IsEligible,
    int CaptureStageIndex,
    int ReuseFromStageIndex);

/// <summary>
/// How the LTX runtime preparers match an injected audio track to video length. The coordinator's
/// non-handoff injection matches a native source to video length even without the checkbox; the
/// root-handoff path only does so for an external source with the checkbox. This asymmetry is LTX
/// runtime behaviour, so it lives with the LTX plan rather than on the generic audio plan.
/// </summary>
internal sealed record Ltx2AudioInjectionPlan(
    bool NonHandoffMatchesAudioLength,
    bool RootHandoffMatchesAudioLength);

internal sealed record Ltx2AudioPlan(
    AudioReusePlan Reuse,
    Ltx2AudioInjectionPlan Injection,
    ImmutableArray<PlanDiagnostic> Diagnostics);

internal static class Ltx2AudioPlanCompiler
{
    internal static Ltx2AudioPlan Compile(
        ClipSpec clip,
        int? controlNetSourceIndex)
    {
        bool external = AudioSourceParser.Parse(clip.AudioSource).Kind
            is AudioSourceKind.Upload or AudioSourceKind.AceStepFun or AudioSourceKind.ControlNet;
        Ltx2AudioInjectionPlan injection = new(
            external ? clip.ClipLengthFromAudio : true,
            external && clip.ClipLengthFromAudio);
        AudioReusePlan reuse = CompileReuse(clip);
        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        if (clip.ClipLengthFromControlNet && controlNetSourceIndex is null)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "audio.length.controlnet_owner_has_no_source",
                "ControlNet owns clip length, but no valid LTX ControlNet 1-3 drive source is "
                    + "configured; the authored clip length will be used instead."));
        }
        return new(
            reuse,
            injection,
            diagnostics.ToImmutable());
    }

    private static AudioReusePlan CompileReuse(ClipSpec clip)
    {
        int stageCount = clip.Stages?.Count ?? 0;
        bool eligible = clip.ReuseAudio
            && stageCount >= Ltx2ClipPolicy.AudioReuseMinimumActiveStages;
        return new(
            clip.ReuseAudio,
            eligible,
            CaptureStageIndex: 1,
            ReuseFromStageIndex: 2);
    }
}
