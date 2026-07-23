using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>Rejects architecture-owned settings before an architecture module receives a clip.</summary>
internal static class ArchitectureCapabilityValidator
{
    internal static IReadOnlyList<VideoPlanDiagnostic> Validate(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        ArchitectureEntryMode entryMode,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels)
    {
        List<VideoPlanDiagnostic> diagnostics = [];
        bool hasActiveStages = clip.Stages is { Count: > 0 };
        void Require(bool configured, bool supported, string option)
        {
            if (configured && !supported)
            {
                diagnostics.Add(new(
                    VideoPlanDiagnosticSeverity.Error,
                    "architecture-capability-unsupported",
                    $"Clip {clip.Id} configures '{option}', which architecture "
                        + $"'{descriptor.Id}' does not support.",
                    clip.Id));
            }
        }

        Require(
            configured: true,
            descriptor.EntryModes.Contains(entryMode),
            $"entry mode '{entryMode}'");
        Require(
            clip.SourceVideo is null,
            Has(
                descriptor.Capabilities.Architecture,
                ArchitectureCapability.GeneratedEntry),
            "generated entry");
        Require(
            clip.SourceVideo is not null,
            Has(
                descriptor.Capabilities.Architecture,
                ArchitectureCapability.SourcedEntry),
            "sourced entry");
        Require(
            clip.Stages is { Count: > 1 },
            Has(
                descriptor.Capabilities.Architecture,
                ArchitectureCapability.MultiStage),
            "multiple active stages");
        Require(
            configured: true,
            Has(
                descriptor.Capabilities.Architecture,
                ArchitectureCapability.DecodedOutput),
            "decoded output");
        Require(
            configured: true,
            Has(descriptor.Capabilities.Output, OutputCapability.Video),
            "video output");
        Require(
            clip.SaveAudioTrack,
            Has(descriptor.Capabilities.Output, OutputCapability.StandaloneAudio),
            "standalone audio output");
        Require(
            clip.Stages is { Count: > 0 }
                && entryMode == ArchitectureEntryMode.ImageToVideo,
            Has(descriptor.Capabilities.Stage, StageCapability.ImageInput),
            "image stage input");
        Require(
            clip.Stages is { Count: > 0 }
                && entryMode is ArchitectureEntryMode.SourceVideo
                    or ArchitectureEntryMode.RefineVideo,
            Has(descriptor.Capabilities.Stage, StageCapability.VideoInput),
            "video stage input");
        Require(
            clip.SourceVideo is not null,
            Has(descriptor.Capabilities.Clip, ClipCapability.SourceVideo),
            "source video");
        Require(
            hasActiveStages && clip.PromptWindows is { Count: > 0 },
            Has(descriptor.Capabilities.Clip, ClipCapability.PromptRelay),
            "prompt relay");
        Require(
            hasActiveStages && clip.ImageRefs is { Count: > 0 },
            Has(descriptor.Capabilities.Clip, ClipCapability.References),
            "image references");
        Require(
            hasActiveStages && clip.ImageRefs is { Count: > 0 },
            Has(descriptor.Capabilities.Stage, StageCapability.FrameReferences),
            "frame references");
        Require(
            clip.Stages?.Any(stage => stage.RetakeWindow is not null) == true,
            Has(descriptor.Capabilities.Clip, ClipCapability.Retake),
            "retake");
        Require(
            clip.UploadedAudio is not null
                || !string.Equals(
                    clip.AudioSource,
                    Constants.AudioSourceNative,
                    StringComparison.OrdinalIgnoreCase),
            Has(descriptor.Capabilities.Clip, ClipCapability.AudioSources),
            "clip audio source");
        Require(
            clip.AudioSegments is { Count: > 0 },
            Has(descriptor.Capabilities.Clip, ClipCapability.AudioSegments),
            "audio segments");
        Require(
            hasActiveStages && clip.IcLoras is { Count: > 0 },
            Has(descriptor.Capabilities.Stage, StageCapability.IcLora),
            "IC-LoRA");
        Require(
            hasActiveStages && (clip.Loras is { Count: > 0 }
                || clip.Stages?.Any(stage => stage.Loras is { Count: > 0 }) == true),
            Has(descriptor.Capabilities.Stage, StageCapability.Lora),
            "stage LoRA");
        ValidateAudioSourceKind(clip, descriptor, diagnostics);
        ValidateStages(clip, descriptor, stageModels, diagnostics);
        return diagnostics.AsReadOnly();
    }

    private static void ValidateAudioSourceKind(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        ICollection<VideoPlanDiagnostic> diagnostics)
    {
        ArchitectureAudioSourceKind? kind = ResolveAudioSourceKind(
            clip.AudioSource,
            descriptor);
        if (kind is null)
        {
            diagnostics.Add(Unsupported(
                clip,
                descriptor,
                $"unknown audio source kind '{clip.AudioSource}'"));
            return;
        }
        if (descriptor.AudioSourceKinds.Contains(kind.Value))
        {
            return;
        }
        diagnostics.Add(Unsupported(
            clip,
            descriptor,
            $"audio source kind '{kind.Value}'"));
    }

    private static ArchitectureAudioSourceKind? ResolveAudioSourceKind(
        string raw,
        VideoArchitectureDescriptor descriptor)
    {
        if (StringUtils.Equals(raw, Constants.AudioSourceNative))
        {
            return descriptor.AudioSourceKinds.Contains(ArchitectureAudioSourceKind.Native)
                ? ArchitectureAudioSourceKind.Native
                : ArchitectureAudioSourceKind.Disabled;
        }
        if (StringUtils.Equals(raw, Constants.AudioSourceUpload))
        {
            return ArchitectureAudioSourceKind.Upload;
        }
        if (StringUtils.Equals(raw, Constants.AudioSourceControlNet))
        {
            return ArchitectureAudioSourceKind.ControlNet;
        }
        return AudioHandler.TryParseAceStepFunAudioSource(raw, out _)
            ? ArchitectureAudioSourceKind.AceStepFun
            : null;
    }

    private static void ValidateStages(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ICollection<VideoPlanDiagnostic> diagnostics)
    {
        IReadOnlyList<StageSpec> stages = clip.Stages ?? [];
        for (int stageIndex = 0; stageIndex < stages.Count; stageIndex++)
        {
            StageSpec stage = stages[stageIndex];
            if (stageIndex > 0
                && !Has(descriptor.Capabilities.Stage, StageCapability.VideoInput))
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
                    || !Has(descriptor.Capabilities.Stage, required))
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

            if (!stageModels.TryGetValue(
                    stage.ClipStageRawIndex,
                    out ResolvedVideoModel resolved))
            {
                continue;
            }
            VideoModelProfileDescriptor profile = descriptor.Profiles.SingleOrDefault(
                candidate => candidate.Id == resolved.ModelProfileId);
            RequireProfileCapability(
                !string.IsNullOrWhiteSpace(stage.Sampler),
                ModelProfileCapability.SamplerSelection,
                "sampler selection",
                clip,
                descriptor,
                stage,
                resolved,
                profile,
                diagnostics);
            RequireProfileCapability(
                !string.IsNullOrWhiteSpace(stage.Scheduler),
                ModelProfileCapability.SchedulerSelection,
                "scheduler selection",
                clip,
                descriptor,
                stage,
                resolved,
                profile,
                diagnostics);
            RequireProfileCapability(
                configured: true,
                ModelProfileCapability.DimensionRules,
                "dimension rules",
                clip,
                descriptor,
                stage,
                resolved,
                profile,
                diagnostics);
            RequireProfileCapability(
                clip.Frames.HasValue,
                ModelProfileCapability.FrameRules,
                "frame rules",
                clip,
                descriptor,
                stage,
                resolved,
                profile,
                diagnostics);
            RequireProfileCapability(
                stage.Loras is { Count: > 0 } || clip.Loras is { Count: > 0 },
                ModelProfileCapability.NormalLora,
                "stage LoRA",
                clip,
                descriptor,
                stage,
                resolved,
                profile,
                diagnostics);
        }
    }

    private static void RequireProfileCapability(
        bool configured,
        ModelProfileCapability capability,
        string option,
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        StageSpec stage,
        ResolvedVideoModel resolved,
        VideoModelProfileDescriptor profile,
        ICollection<VideoPlanDiagnostic> diagnostics)
    {
        if (configured
            && (profile is null || !Has(profile.Capabilities, capability)))
        {
            diagnostics.Add(Unsupported(
                clip,
                descriptor,
                $"{option} for model profile '{resolved.ModelProfileId}'",
                stage.Id));
        }
    }

    private static VideoPlanDiagnostic Unsupported(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        string option,
        int? stageId = null) =>
        new(
            VideoPlanDiagnosticSeverity.Error,
            "architecture-capability-unsupported",
            $"Clip {clip.Id} configures '{option}', which architecture "
                + $"'{descriptor.Id}' does not support.",
            clip.Id,
            stageId);

    private static bool Has<T>(T value, T capability) where T : struct, Enum =>
        (Convert.ToInt64(value) & Convert.ToInt64(capability))
            == Convert.ToInt64(capability);

}
