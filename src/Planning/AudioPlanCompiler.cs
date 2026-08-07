using System.Collections.Immutable;
using VideoStages.Authoring;

namespace VideoStages.Planning;

internal static class AudioPlanCompiler
{
    private const string UnknownSourceCode = "audio.source.unknown";
    private const string ControlNetOverridesAudioLength =
        "audio.length.controlnet_overrides_audio";
    private const string AudioLengthWithoutTrack =
        "audio.length.audio_owner_has_no_lockable_track";

    public static AudioPlan Compile(ClipSpec clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        AudioSource selection = AudioSource.Parse(clip.AudioSource);
        AudioSourceKind sourceKind = selection.Kind;
        bool hasConfiguredTrack;
        if (sourceKind == AudioSourceKind.Unknown)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                UnknownSourceCode,
                $"Audio source '{selection.Raw}' is not supported; audio is disabled for this clip.",
                clip.Id));
            sourceKind = AudioSourceKind.Disabled;
            hasConfiguredTrack = false;
        }
        else
        {
            hasConfiguredTrack = sourceKind != AudioSourceKind.Upload
                || !string.IsNullOrWhiteSpace(clip.UploadedAudio?.Data);
        }
        AudioBaseSourcePlan baseSource = new(
            sourceKind,
            selection.Raw,
            selection.AceStepFunTrack,
            hasConfiguredTrack,
            sourceKind == AudioSourceKind.Upload
                ? clip.UploadedAudio
                : null);

        AudioLengthOwner lengthOwner;
        if (clip.ClipLengthFromControlNet)
        {
            lengthOwner = AudioLengthOwner.ControlNet;
            if (clip.ClipLengthFromAudio)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    ControlNetOverridesAudioLength,
                    "ControlNet owns clip length when both ControlNet and audio length are requested."));
            }
        }
        else if (clip.ClipLengthFromAudio
            && AudioSourceKindPolicy.CanDriveClipDuration(sourceKind))
        {
            lengthOwner = AudioLengthOwner.Audio;
            if (!baseSource.HasConfiguredTrack)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    AudioLengthWithoutTrack,
                    "Audio owns clip length, but the selected audio source does not provide a locked track."));
            }
        }
        else
        {
            lengthOwner = AudioLengthOwner.Timeline;
        }

        // Root timeline spans are added after boundary planning.
        return new(
            baseSource,
            lengthOwner,
            [],
            diagnostics.ToImmutable());
    }
}
