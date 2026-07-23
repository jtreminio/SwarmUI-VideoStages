using System.Collections.Immutable;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

internal enum AudioVoiceReferenceKind
{
    None,
    ClipUpload,
    IcLoraDriveVideo
}

internal sealed record AudioVoiceReferencePlan(
    AudioVoiceReferenceKind Kind,
    bool IsRequested,
    bool HasConfiguredSample,
    AudioMediaIdentityPlan Media,
    int? IcLoraEntryIndex,
    IcLoraUploadedMediaKind? DriveMediaKind,
    AudioMediaIdentityPlan FallbackMedia);

internal sealed record AudioReusePlan(
    bool IsRequested,
    bool IsEligible,
    int CaptureStageIndex,
    int ReuseFromStageIndex);

internal sealed record Ltx2AudioPlan(
    AudioVoiceReferencePlan VoiceReference,
    AudioReusePlan Reuse,
    int? ControlNetSourceIndex,
    ImmutableArray<AudioPlanDiagnostic> Diagnostics);

internal static class Ltx2AudioPlanCompiler
{
    internal static Ltx2AudioPlan Compile(ClipSpec clip)
    {
        AudioPlanComponentResult<AudioVoiceReferencePlan> voice =
            AudioVoiceReferencePlanCompiler.Compile(clip);
        AudioPlanComponentResult<AudioReusePlan> reuse =
            AudioReusePlanCompiler.Compile(clip);
        int? controlNetSourceIndex =
            IcLoraPlanCompiler.ResolvePrimaryControlNetSourceIndex(clip);
        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<AudioPlanDiagnostic>();
        diagnostics.AddRange(voice.Diagnostics);
        diagnostics.AddRange(reuse.Diagnostics);
        if (clip.ClipLengthFromControlNet && controlNetSourceIndex is null)
        {
            diagnostics.Add(new(
                "audio.length.controlnet_owner_has_no_source",
                "ControlNet owns clip length, but no valid LTX ControlNet 1-3 drive source is configured."));
        }
        return new(
            voice.Plan,
            reuse.Plan,
            controlNetSourceIndex,
            diagnostics.ToImmutable());
    }
}
