using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Compiles only the configured lockable base audio source.</summary>
internal static class AudioBaseSourcePlanCompiler
{
    internal const string UnknownSourceCode = "audio.source.unknown";

    internal static AudioPlanComponentResult<AudioBaseSourcePlan> Compile(ClipSpec clip)
    {
        AudioSourceSelection selection = AudioSourceParser.Parse(clip.AudioSource);
        // An unrecognised source is the one blocking outcome: quietly generating with native audio
        // would publish something the author never asked for.
        if (selection.Kind == AudioSourceKind.Unknown)
        {
            return new(
                new(selection.Kind, selection.Raw, null, HasConfiguredTrack: false, null),
                [new(
                    PlanDiagnosticSeverity.Error,
                    UnknownSourceCode,
                    $"Audio source '{selection.Raw}' is not a supported audio source.",
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
