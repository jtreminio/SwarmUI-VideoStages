using System.Collections.Immutable;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>
/// Controls when LTX injection matches audio length. Non-handoff native audio always matches;
/// external audio matches only when clip-length-from-audio is enabled. Root handoff uses only the
/// external-audio rule.
/// </summary>
internal sealed record Ltx2AudioInjectionPlan(
    bool NonHandoffMatchesAudioLength,
    bool RootHandoffMatchesAudioLength);

internal sealed record Ltx2AudioPlan(
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
            injection,
            diagnostics.ToImmutable());
    }
}
