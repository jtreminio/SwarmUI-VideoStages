using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>Reports authored settings that the resolved architecture cannot use.</summary>
internal static class ArchitectureCapabilityValidator
{
    internal static IReadOnlyList<PlanDiagnostic> Validate(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        ArchitectureEntryMode entryMode,
        bool hasOutgoingBoundary = true)
    {
        List<PlanDiagnostic> diagnostics = [];
        if (!descriptor.EntryModes.Contains(entryMode))
        {
            diagnostics.Add(Unsupported(
                clip,
                descriptor,
                $"{ArchitectureFeatureVocabulary.WireName(entryMode)} entry"));
        }
        WarnAboutUnsupportedFeatures(
            clip,
            descriptor,
            diagnostics,
            hasOutgoingBoundary);
        if (descriptor.Features.HasFlag(ArchitectureFeature.AudioDerivedDuration))
        {
            ValidateAudioDerivedDurationSource(clip, diagnostics);
        }
        return diagnostics.AsReadOnly();
    }

    private static void WarnAboutUnsupportedFeatures(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        ICollection<PlanDiagnostic> diagnostics,
        bool hasOutgoingBoundary)
    {
        IReadOnlyList<StageSpec> stages = clip.Stages ?? [];
        bool Unsupported(ArchitectureFeature feature) =>
            !descriptor.Features.HasFlag(feature);
        void Warn(
            bool configured,
            ArchitectureFeature feature,
            string description,
            int? stageId = null,
            int? rawStageIndex = null)
        {
            if (!configured)
            {
                return;
            }
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                $"effective-request.unsupported-{DiagnosticKey(feature)}-ignored",
                $"Clip {clip.Id}{(stageId.HasValue ? $" Stage {stageId}" : "")} "
                    + $"configures {description}, which {descriptor.DisplayName} does not "
                    + "support. The authored setting remains saved and is ignored for this "
                    + "generation.",
                clip.Id,
                stageId,
                rawStageIndex));
        }

        Warn(
            Unsupported(ArchitectureFeature.FrameReferences)
                && (clip.ImageRefs is { Count: > 0 }
                    || stages.Any(stage => stage.ImageRefStrengths is { Count: > 0 })),
            ArchitectureFeature.FrameReferences,
            "image/frame references");
        Warn(
            Unsupported(ArchitectureFeature.PromptRelay)
                && clip.PromptWindows is { Count: > 0 },
            ArchitectureFeature.PromptRelay,
            "prompt relay windows");
        Warn(
            Unsupported(ArchitectureFeature.Retake)
                && stages.Any(stage => stage.RetakeWindow is not null),
            ArchitectureFeature.Retake,
            "a retake window");
        Warn(
            Unsupported(ArchitectureFeature.ReferenceFraming)
                && clip.ReferenceFraming != ReferenceFramingMode.Crop,
            ArchitectureFeature.ReferenceFraming,
            "non-default reference framing");
        Warn(
            Unsupported(ArchitectureFeature.IcLora)
                && (clip.IcLoras is { Count: > 0 }
                    || stages.Any(stage => stage.IcLoraStrengths is { Count: > 0 })),
            ArchitectureFeature.IcLora,
            "IC-LoRA data");
        Warn(
            Unsupported(ArchitectureFeature.IcLora)
                && clip.ClipLengthFromControlNet,
            ArchitectureFeature.IcLora,
            "control-signal-derived clip duration");
        Warn(
            Unsupported(ArchitectureFeature.AudioDerivedDuration)
                && clip.ClipLengthFromAudio,
            ArchitectureFeature.AudioDerivedDuration,
            "audio-derived clip duration");
        Warn(
            Unsupported(ArchitectureFeature.AudioReuse) && clip.ReuseAudio,
            ArchitectureFeature.AudioReuse,
            "captured stage audio reuse");

        AudioSourceKind authoredAudioKind =
            AudioSourceParser.Parse(clip.AudioSource).Kind;
        if (authoredAudioKind != AudioSourceKind.Unknown
            && !descriptor.AudioSourceKinds.Contains(authoredAudioKind)
            && !(authoredAudioKind == AudioSourceKind.Native
                && descriptor.AudioSourceKinds.Contains(AudioSourceKind.Disabled)))
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.unsupported-audio-source-ignored",
                $"Clip {clip.Id} configures audio source kind '{authoredAudioKind}', which "
                    + $"{descriptor.DisplayName} does not support. The authored setting remains "
                    + "saved and is ignored for this generation.",
                clip.Id));
        }
        if (!descriptor.AudioSourceKinds.Contains(AudioSourceKind.Native)
            && clip.SaveAudioTrack)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.unsupported-audio-output-ignored",
                $"Clip {clip.Id} configures standalone audio output, which "
                    + $"{descriptor.DisplayName} does not support. The authored setting remains "
                    + "saved and is ignored for this generation.",
                clip.Id));
        }
        // Carry audio only ever acts on a non-cut join, so a cut boundary has nothing to refuse.
        if (hasOutgoingBoundary
            && Unsupported(ArchitectureFeature.AudioBoundaryCarry)
            && clip.BoundaryOutCarryAudio
            && !StringUtils.Equals(clip.BoundaryOut, Constants.BoundaryOutCut))
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "effective-request.unsupported-audio-boundary-ignored",
                $"Clip {clip.Id} configures audio boundary carry, which "
                    + $"{descriptor.DisplayName} does not support. The authored setting remains "
                    + "saved and is ignored for this generation.",
                clip.Id));
        }

        for (int index = 0; index < stages.Count; index++)
        {
            StageSpec stage = stages[index];
            StageGuideReferenceSelection guide =
                StageGuideReferencePolicy.Classify(stage.ImageReference);
            if (!descriptor.StageGuideReferences.Allows(guide))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "effective-request.unsupported-stage-reference-ignored",
                    $"Clip {clip.Id} Stage {stage.Id} uses image selector "
                        + $"'{stage.ImageReference}', which {descriptor.DisplayName} does not "
                        + "support. The authored selector remains saved and is ignored for this "
                        + "generation.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            StageUpscaleMode upscaleMode =
                StageUpscalePlanCompiler.Classify(stage.UpscaleMethod);
            if (stage.Upscale != 1 && upscaleMode == StageUpscaleMode.Unsupported)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "effective-request.unknown-upscale",
                    $"Clip {clip.Id} Stage {stage.Id} uses unknown upscale mode "
                        + $"'{stage.UpscaleMethod}', which is ignored for this generation.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            else if (stage.Upscale != 1
                && ((upscaleMode == StageUpscaleMode.Latent
                        && Unsupported(ArchitectureFeature.LatentUpscale))
                    || (upscaleMode == StageUpscaleMode.LatentModel
                        && Unsupported(ArchitectureFeature.LatentModelUpscale))))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "effective-request.unsupported-latent-upscale-ignored",
                    $"Clip {clip.Id} Stage {stage.Id} uses latent upscale mode "
                        + $"'{stage.UpscaleMethod}', which {descriptor.DisplayName} does not "
                        + "support. The authored value remains saved and is ignored for this "
                        + "generation.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
        }
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
            PlanDiagnosticSeverity.Warning,
            "audio.length.source_cannot_drive_duration",
            $"Clip {clip.Id} configures audio-derived duration, but audio source kind "
                + $"'{kind}' cannot determine video duration. The authored setting remains "
                + "saved and the authored clip length is used for this generation.",
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

    private static string DiagnosticKey(ArchitectureFeature feature)
    {
        StringBuilder result = new();
        foreach (char value in ArchitectureFeatureVocabulary.WireName(feature))
        {
            if (char.IsUpper(value))
            {
                result.Append('-');
            }
            result.Append(char.ToLowerInvariant(value));
        }
        return result.ToString();
    }
}
