using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>
/// The last word on facts <see cref="CapabilityDrivenEffectiveRequestProjector"/> cannot safely
/// drop: entry mode and audio-source kind decide which models execute, and a malformed stage
/// selector has no supported default. Every optional feature is already projected away by then,
/// so re-checking one here could only ever restate a value the projector reset.
/// </summary>
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
        ValidateAudioSourceKind(clip, descriptor, diagnostics);
        ValidateStageGuideReferences(clip, descriptor, diagnostics);
        return diagnostics.AsReadOnly();
    }

    private static void ValidateAudioDerivedDurationSource(
        ClipSpec clip,
        ICollection<PlanDiagnostic> diagnostics)
    {
        if (!clip.ClipLengthFromAudio)
        {
            return;
        }
        AudioSourceKind kind = AudioSourceParser.Parse(clip.AudioSource).Kind;
        if (kind == AudioSourceKind.Unknown
            || AudioSourceKindPolicy.CanDriveClipDuration(kind))
        {
            // AudioBaseSourcePlanCompiler remains the sole owner of unknown-source diagnostics.
            return;
        }
        diagnostics.Add(new(
            PlanDiagnosticSeverity.Error,
            "audio.length.source_cannot_drive_duration",
            $"Clip {clip.Id} configures audio-derived duration, but audio source kind "
                + $"'{kind}' cannot determine video duration.",
            clip.Id));
    }

    private static void ValidateAudioSourceKind(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        ICollection<PlanDiagnostic> diagnostics)
    {
        IReadOnlyList<AudioSourceKind> audioSourceKinds = descriptor.AudioSourceKinds;
        AudioSourceKind kind = AudioSourceParser.Parse(clip.AudioSource).Kind;
        if (kind == AudioSourceKind.Unknown)
        {
            // AudioBaseSourcePlanCompiler owns the unknown-source error for every clip, whether or
            // not an architecture was resolved, so re-reporting it here would only duplicate it.
            return;
        }
        if (kind == AudioSourceKind.Native
            && !audioSourceKinds.Contains(AudioSourceKind.Native))
        {
            kind = AudioSourceKind.Disabled;
        }
        if (audioSourceKinds.Contains(kind))
        {
            return;
        }
        diagnostics.Add(Unsupported(
            clip,
            descriptor,
            $"audio source kind '{kind}'"));
    }

    /// <summary>
    /// The projector rewrites every selector it can read, so only malformed syntax survives to
    /// here.
    /// </summary>
    private static void ValidateStageGuideReferences(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        ICollection<PlanDiagnostic> diagnostics)
    {
        foreach (StageSpec stage in clip.Stages ?? [])
        {
            if (!descriptor.StageGuideReferences.Allows(
                StageGuideReferencePolicy.Classify(stage.ImageReference)))
            {
                diagnostics.Add(Unsupported(
                    clip,
                    descriptor,
                    $"stage image reference '{stage.ImageReference}'",
                    stage.Id));
            }
        }
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
