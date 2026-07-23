using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Compiles only the configured lockable base audio source.</summary>
internal static class AudioBaseSourcePlanCompiler
{
    private const string UnknownSourceDefaultsToNative = "audio.source.unknown_defaults_to_native";

    internal static AudioPlanComponentResult<AudioBaseSourcePlan> Compile(ClipSpec clip)
    {
        string raw = (clip.AudioSource ?? Constants.AudioSourceNative).Trim();
        if (raw.Length == 0 || StringUtils.Equals(raw, Constants.AudioSourceNative))
        {
            return Result(new(AudioBaseSourceKind.Native, raw, null, HasConfiguredTrack: true, null));
        }
        if (StringUtils.Equals(raw, Constants.AudioSourceVoiceRef))
        {
            return Result(new(AudioBaseSourceKind.None, raw, null, HasConfiguredTrack: false, null));
        }
        if (StringUtils.Equals(raw, Constants.AudioSourceUpload))
        {
            return Result(new(
                AudioBaseSourceKind.Upload,
                raw,
                null,
                HasConfiguredTrack: !string.IsNullOrWhiteSpace(clip.UploadedAudio?.Data),
                AudioMediaIdentityCompiler.Compile(clip.UploadedAudio)));
        }
        if (StringUtils.Equals(raw, Constants.AudioSourceControlNet))
        {
            return Result(new(AudioBaseSourceKind.ControlNet, raw, null, HasConfiguredTrack: true, null));
        }
        if (AudioHandler.TryParseAceStepFunAudioSource(raw, out int track))
        {
            return Result(new(AudioBaseSourceKind.AceStepFun, raw, track, HasConfiguredTrack: true, null));
        }

        return new(
            new(AudioBaseSourceKind.Native, raw, null, HasConfiguredTrack: true, null),
            [new(
                UnknownSourceDefaultsToNative,
                $"Audio source '{raw}' is not a supported external source, so it follows the native-audio path.")]);
    }

    private static AudioPlanComponentResult<AudioBaseSourcePlan> Result(AudioBaseSourcePlan plan) =>
        new(plan, ImmutableArray<AudioPlanDiagnostic>.Empty);
}
