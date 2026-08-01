using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>Rejects architecture-owned settings before an architecture module receives a clip.</summary>
internal static class ArchitectureCapabilityValidator
{
    internal static IReadOnlyList<PlanDiagnostic> Validate(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        ArchitectureEntryMode entryMode,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels)
    {
        List<PlanDiagnostic> diagnostics = [];
        bool hasActiveStages = clip.Stages is { Count: > 0 };
        ArchitectureCapabilityDescriptor capabilities = descriptor.Capabilities;
        IReadOnlyList<AudioSourceKind> audioSourceKinds = descriptor.AudioSourceKinds;
        void Require(bool configured, bool supported, string option)
        {
            if (configured && !supported)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "architecture-capability-unsupported",
                    $"Clip {clip.Id} configures '{option}', which architecture "
                        + $"'{descriptor.Id}' does not support.",
                    clip.Id));
            }
        }

        if (hasActiveStages)
        {
            ValidateModelEntryRoles(
                clip,
                descriptor,
                entryMode,
                stageModels,
                diagnostics);
        }
        Require(
            configured: true,
            descriptor.EntryModes.Contains(entryMode),
            $"{ArchitectureFeatureVocabulary.WireName(entryMode)} entry");
        Require(
            clip.SaveAudioTrack,
            audioSourceKinds.Contains(AudioSourceKind.Native),
            "standalone audio output");
        Require(
            hasActiveStages && clip.PromptWindows is { Count: > 0 },
            Has(capabilities.Clip, ClipCapability.PromptRelay),
            "prompt relay");
        Require(
            hasActiveStages && clip.ImageRefs is { Count: > 0 },
            Has(capabilities.Clip, ClipCapability.References),
            "image references");
        Require(
            hasActiveStages && clip.ImageRefs is { Count: > 0 },
            Has(capabilities.Stage, StageCapability.FrameReferences),
            "frame references");
        Require(
            clip.ReferenceFraming != ReferenceFramingMode.Crop,
            Has(capabilities.Clip, ClipCapability.ReferenceFraming),
            "reference framing");
        Require(
            clip.Stages?.Any(stage => stage.RetakeWindow is not null) == true,
            Has(capabilities.Clip, ClipCapability.Retake),
            "retake");
        Require(
            clip.UploadedAudio is not null
                || !string.Equals(
                    clip.AudioSource,
                    Constants.AudioSourceNative,
                    StringComparison.OrdinalIgnoreCase),
            Has(capabilities.Clip, ClipCapability.AudioSources),
            "clip audio source");
        bool supportsAudioDerivedDuration = Has(
            capabilities.Clip,
            ClipCapability.AudioDerivedDuration);
        Require(
            clip.ClipLengthFromAudio,
            supportsAudioDerivedDuration,
            "audio-derived clip duration");
        ValidateAudioDerivedDurationSource(
            clip,
            supportsAudioDerivedDuration,
            diagnostics);
        Require(
            clip.ClipLengthFromControlNet,
            Has(
                capabilities.Clip,
                ClipCapability.ControlSignalDerivedDuration),
            "control-signal-derived clip duration");
        Require(
            clip.ReuseAudio,
            Has(capabilities.Clip, ClipCapability.AudioReuse),
            "captured stage audio reuse");
        Require(
            hasActiveStages && clip.IcLoras is { Count: > 0 },
            Has(capabilities.Stage, StageCapability.IcLora),
            "IC-LoRA");
        ValidateAudioSourceKind(clip, descriptor, audioSourceKinds, diagnostics);
        ValidateStages(clip, descriptor, diagnostics);
        return diagnostics.AsReadOnly();
    }

    private static void ValidateModelEntryRoles(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        ArchitectureEntryMode entryMode,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ICollection<PlanDiagnostic> diagnostics)
    {
        for (int activeStageIndex = 0; activeStageIndex < clip.Stages.Count; activeStageIndex++)
        {
            StageSpec stage = clip.Stages[activeStageIndex];
            bool resolvedForArchitecture =
                stageModels.TryGetValue(
                    stage.ClipStageRawIndex,
                    out ResolvedVideoModel resolved)
                && resolved is not null
                && resolved.ArchitectureId == descriptor.Id
                && ReferenceEquals(resolved.Architecture, descriptor);
            if (resolvedForArchitecture
                && VideoModelEntryPolicy.SupportsStageRole(
                    resolved,
                    activeStageIndex,
                    entryMode))
            {
                continue;
            }
            string required = activeStageIndex == 0
                ? entryMode.ToString()
                : ArchitectureEntryMode.ImageToVideo.ToString();
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Error,
                "architecture-model-entry-unsupported",
                $"Clip {clip.Id} stage {stage.Id} model "
                    + $"'{resolved?.ModelName ?? stage.Model}' cannot perform its "
                    + (activeStageIndex == 0 ? "clip-root" : "decoded later-stage")
                    + $" role, which requires entry ability '{required}'.",
                clip.Id,
                stage.Id,
                stage.ClipStageRawIndex));
        }
    }

    private static void ValidateAudioDerivedDurationSource(
        ClipSpec clip,
        bool capabilitySupported,
        ICollection<PlanDiagnostic> diagnostics)
    {
        if (!clip.ClipLengthFromAudio || !capabilitySupported)
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
        IReadOnlyList<AudioSourceKind> audioSourceKinds,
        ICollection<PlanDiagnostic> diagnostics)
    {
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

    private static void ValidateStages(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        ICollection<PlanDiagnostic> diagnostics)
    {
        IReadOnlyList<StageSpec> stages = clip.Stages ?? [];
        for (int stageIndex = 0; stageIndex < stages.Count; stageIndex++)
        {
            StageSpec stage = stages[stageIndex];
            StageGuideReferenceSelection guide =
                StageGuideReferencePolicy.Classify(stage.ImageReference);
            if (!descriptor.StageGuideReferences.Allows(guide))
            {
                diagnostics.Add(Unsupported(
                    clip,
                    descriptor,
                    $"stage image reference '{stage.ImageReference}'",
                    stage.Id));
            }
            // Upscale methods are architecture-neutral; only an unrecognized method is refused.
            if (stage.Upscale != 1
                && StageUpscalePlanCompiler.Classify(stage.UpscaleMethod)
                    == StageUpscaleMode.Unsupported)
            {
                diagnostics.Add(Unsupported(
                    clip,
                    descriptor,
                    $"unknown upscale mode '{stage.UpscaleMethod}'",
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

    private static bool Has<T>(T value, T capability) where T : struct, Enum =>
        (Convert.ToInt64(value) & Convert.ToInt64(capability))
            == Convert.ToInt64(capability);

}
