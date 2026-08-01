using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Compiles a clip's selected base audio source.</summary>
internal static class AudioBaseSourcePlanCompiler
{
    internal const string UnknownSourceCode = "audio.source.unknown";

    internal static AudioPlanComponentResult<AudioBaseSourcePlan> Compile(ClipSpec clip)
    {
        AudioSourceSelection selection = AudioSourceParser.Parse(clip.AudioSource);
        if (selection.Kind == AudioSourceKind.Unknown)
        {
            return new(
                new(AudioSourceKind.Disabled, selection.Raw, null, HasConfiguredTrack: false, null),
                [new(
                    PlanDiagnosticSeverity.Warning,
                    UnknownSourceCode,
                    $"Audio source '{selection.Raw}' is not supported; audio is disabled for this clip.",
                    clip.Id)]);
        }
        return Result(new(
            selection.Kind,
            selection.Raw,
            selection.AceStepFunTrack,
            HasConfiguredTrack: selection.Kind != AudioSourceKind.Upload
                || !string.IsNullOrWhiteSpace(clip.UploadedAudio?.Data),
            selection.Kind == AudioSourceKind.Upload
                ? AudioMediaIdentityPlan.From(clip.UploadedAudio)
                : null));
    }

    private static AudioPlanComponentResult<AudioBaseSourcePlan> Result(AudioBaseSourcePlan plan) =>
        new(plan, ImmutableArray<PlanDiagnostic>.Empty);
}
