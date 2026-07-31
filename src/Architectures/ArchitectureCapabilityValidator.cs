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
            clip.SourceVideo is null,
            Has(
                capabilities.Architecture,
                ArchitectureCapability.GeneratedEntry),
            "generated entry");
        Require(
            clip.SourceVideo is not null,
            Has(
                capabilities.Architecture,
                ArchitectureCapability.SourcedEntry),
            "sourced entry");
        Require(
            clip.Stages is { Count: > 1 },
            Has(
                capabilities.Architecture,
                ArchitectureCapability.MultiStage),
            "multiple active stages");
        Require(
            clip.SaveAudioTrack,
            Has(
                capabilities.Architecture,
                ArchitectureCapability.NativeAudio)
                && audioSourceKinds.Contains(AudioSourceKind.Native),
            "standalone audio output");
        Require(
            clip.Stages is { Count: > 0 }
                && entryMode == ArchitectureEntryMode.ImageToVideo,
            Has(capabilities.Stage, StageCapability.ImageInput),
            "image stage input");
        Require(
            clip.Stages is { Count: > 0 }
                && entryMode is ArchitectureEntryMode.SourceVideo
                    or ArchitectureEntryMode.RefineVideo,
            Has(capabilities.Stage, StageCapability.VideoInput),
            "video stage input");
        Require(
            clip.SourceVideo is not null,
            Has(capabilities.Clip, ClipCapability.SourceVideo),
            "source video");
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
        Require(
            hasActiveStages
                && clip.Stages?.Any(stage => HasNormalLora(clip, stage)) == true,
            Has(capabilities.Stage, StageCapability.Lora),
            "normal LoRA");
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
            StageCapability stageCapabilities = descriptor.Capabilities.Stage;
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
            if (stageIndex > 0
                && !Has(stageCapabilities, StageCapability.VideoInput))
            {
                diagnostics.Add(Unsupported(
                    clip,
                    descriptor,
                    "video stage input for a later stage",
                    stage.Id));
            }
            if (stage.Upscale != 1)
            {
                StageCapability required = stage.IsPixelUpscale
                    ? StageCapability.PixelUpscale
                    : stage.IsModelUpscale
                        ? StageCapability.ModelUpscale
                        : stage.IsLatentUpscale
                            ? StageCapability.LatentUpscale
                            : stage.IsLatentModelUpscale
                                ? StageCapability.LatentModelUpscale
                                : StageCapability.None;
                if (required == StageCapability.None
                    || !Has(stageCapabilities, required))
                {
                    diagnostics.Add(Unsupported(
                        clip,
                        descriptor,
                        required == StageCapability.None
                            ? $"unknown upscale mode '{stage.UpscaleMethod}'"
                            : $"upscale mode '{required}'",
                        stage.Id));
                }
            }
        }
    }

    private static bool HasNormalLora(ClipSpec clip, StageSpec stage)
    {
        if (stage.Loras is { Count: > 0 })
        {
            return true;
        }
        if (clip.Loras is not { Count: > 0 })
        {
            return false;
        }
        if (stage.LoraWeights is null)
        {
            return true;
        }
        for (int index = 0; index < clip.Loras.Count; index++)
        {
            double weight = index < stage.LoraWeights.Count
                ? stage.LoraWeights[index]
                : clip.Loras[index].Weight;
            if (weight != 0)
            {
                return true;
            }
        }
        return false;
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
