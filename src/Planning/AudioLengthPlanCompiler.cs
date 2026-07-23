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
        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<AudioPlanDiagnostic>();
        AudioLengthOwner owner;
        if (clip.ClipLengthFromControlNet)
        {
            owner = AudioLengthOwner.ControlNet;
            if (clip.ClipLengthFromAudio)
            {
                diagnostics.Add(new(
                    ControlNetOverridesAudioLength,
                    "ControlNet owns clip length when both ControlNet and audio length are requested."));
            }
        }
        else if (clip.ClipLengthFromAudio)
        {
            owner = AudioLengthOwner.Audio;
            if (!baseSource.HasConfiguredTrack)
            {
                diagnostics.Add(new(
                    AudioLengthWithoutTrack,
                    "Audio owns clip length, but the selected audio source does not provide a locked track."));
            }
        }
        else
        {
            owner = AudioLengthOwner.Timeline;
        }

        // Preserve the existing compatibility behavior while separating it from the user-facing
        // duration owner above. The coordinator's non-handoff injection matches a native source to
        // video length even without the checkbox; the root-handoff path only does so for an external
        // source with the checkbox. The executor can retire this asymmetry deliberately later, but
        // it must not rediscover it from graph state today.
        bool external = baseSource.Kind is AudioBaseSourceKind.Upload
            or AudioBaseSourceKind.AceStepFun
            or AudioBaseSourceKind.ControlNet;
        return new(
            new(
                owner,
                external ? clip.ClipLengthFromAudio : true,
                external && clip.ClipLengthFromAudio),
            diagnostics.ToImmutable());
    }
}
