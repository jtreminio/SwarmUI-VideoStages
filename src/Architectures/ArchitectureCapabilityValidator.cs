using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>Validates architecture constraints that remain after effective-request projection.</summary>
internal static class ArchitectureCapabilityValidator
{
    internal static IReadOnlyList<PlanDiagnostic> Validate(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        ArchitectureEntryMode entryMode)
    {
        List<PlanDiagnostic> diagnostics = [];
        if (!descriptor.EntryModes.Contains(entryMode))
        {
            diagnostics.Add(Unsupported(
                clip,
                descriptor,
                $"{ArchitectureFeatureVocabulary.WireName(entryMode)} entry"));
        }
        ValidateAudioDerivedDurationSource(clip, diagnostics);
        return diagnostics.AsReadOnly();
    }

    private static void ValidateAudioDerivedDurationSource(
        ClipSpec clip,
        ICollection<PlanDiagnostic> diagnostics)
    {
        if (!clip.ClipLengthFromAudio || clip.ClipLengthFromControlNet)
        {
            return;
        }
        AudioSourceKind kind = AudioSourceParser.Parse(clip.AudioSource).Kind;
        if (kind == AudioSourceKind.Unknown
            || AudioSourceKindPolicy.CanDriveClipDuration(kind))
        {
            // Unknown sources are normalized by AudioBaseSourcePlanCompiler.
            return;
        }
        diagnostics.Add(new(
            PlanDiagnosticSeverity.Error,
            "audio.length.source_cannot_drive_duration",
            $"Clip {clip.Id} configures audio-derived duration, but audio source kind "
                + $"'{kind}' cannot determine video duration.",
            clip.Id));
    }

    private static PlanDiagnostic Unsupported(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        string option,
        int? stageId = null) =>
        new(
            PlanDiagnosticSeverity.Error,
            "architecture-capability-unsupported",
            $"Clip {clip.Id} configures '{option}', which architecture "
                + $"'{descriptor.Id}' does not support.",
            clip.Id,
            stageId);
}
