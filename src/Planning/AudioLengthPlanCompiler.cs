using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Compiles duration ownership and route-compatible audio-length behavior.</summary>
internal static class AudioLengthPlanCompiler
{
    private const string ControlNetOverridesAudioLength = "audio.length.controlnet_overrides_audio";
    private const string AudioLengthWithoutTrack = "audio.length.audio_owner_has_no_lockable_track";

    internal static AudioPlanComponentResult<AudioLengthPlan> Compile(
        ClipSpec clip,
        AudioBaseSourcePlan baseSource)
    {
        ImmutableArray<PlanDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();
        AudioLengthOwner owner;
        if (clip.ClipLengthFromControlNet)
        {
            owner = AudioLengthOwner.ControlNet;
            if (clip.ClipLengthFromAudio)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    ControlNetOverridesAudioLength,
                    "ControlNet owns clip length when both ControlNet and audio length are requested."));
            }
        }
        else if (clip.ClipLengthFromAudio)
        {
            owner = AudioLengthOwner.Audio;
            if (baseSource.Kind != AudioSourceKind.Disabled
                && !baseSource.HasConfiguredTrack)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    AudioLengthWithoutTrack,
                    "Audio owns clip length, but the selected audio source does not provide a locked track."));
            }
        }
        else
        {
            owner = AudioLengthOwner.Timeline;
        }

        return new(new(owner), diagnostics.ToImmutable());
    }
}
