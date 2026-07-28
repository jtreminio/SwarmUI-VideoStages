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
            clip.ReferenceFraming != ReferenceFramingMode.Crop,
            Has(descriptor.Capabilities.Clip, ClipCapability.ReferenceFraming),
            "reference framing");
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
            hasActiveStages && clip.IcLoras is { Count: > 0 },
            Has(descriptor.Capabilities.Stage, StageCapability.IcLora),
            "IC-LoRA");
        Require(
            hasActiveStages
                && clip.Stages?.Any(stage => HasNormalLora(clip, stage)) == true,
            Has(descriptor.Capabilities.Stage, StageCapability.Lora),
            "normal LoRA");
        ValidateAudioSourceKind(clip, descriptor, diagnostics);
        ValidateStages(clip, descriptor, stageModels, diagnostics);
        return diagnostics.AsReadOnly();
    }

    /// <summary>
    /// Audio segments are no longer authored on a clip: the root timeline audio tracks are
    /// projected onto each clip's plan after clip compilation, so the capability can only be
    /// checked against that projection.
    /// </summary>
    internal static IReadOnlyList<PlanDiagnostic> ValidateProjectedAudioSegments(
        ClipPlan clip,
        VideoArchitectureDescriptor descriptor) =>
        clip.Audio.Segments.Items.IsEmpty
            || Has(descriptor.Capabilities.Clip, ClipCapability.AudioSegments)
            ? []
            : [new(
                PlanDiagnosticSeverity.Error,
                "architecture-capability-unsupported",
                $"Clip {clip.ClipId} configures 'audio segments', which architecture "
                    + $"'{descriptor.Id}' does not support.",
                clip.ClipId)];

    private static void ValidateAudioSourceKind(
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
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
            && !descriptor.AudioSourceKinds.Contains(AudioSourceKind.Native))
        {
            kind = AudioSourceKind.Disabled;
        }
        if (descriptor.AudioSourceKinds.Contains(kind))
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
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ICollection<PlanDiagnostic> diagnostics)
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
                HasNormalLora(clip, stage),
                ModelProfileCapability.NormalLora,
                "normal LoRA",
                clip,
                descriptor,
                stage,
                resolved,
                profile,
                diagnostics);
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

    private static void RequireProfileCapability(
        bool configured,
        ModelProfileCapability capability,
        string option,
        ClipSpec clip,
        VideoArchitectureDescriptor descriptor,
        StageSpec stage,
        ResolvedVideoModel resolved,
        VideoModelProfileDescriptor profile,
        ICollection<PlanDiagnostic> diagnostics)
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
