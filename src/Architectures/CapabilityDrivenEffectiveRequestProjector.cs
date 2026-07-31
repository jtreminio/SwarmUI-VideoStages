using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>The effective clip and typed dispositions produced by a pure projection.</summary>
internal sealed record EffectiveClipProjection(
    ClipSpec Clip,
    IReadOnlyList<EffectiveRequestDecision> Decisions);

/// <summary>
/// Common graph-free projection of optional authored values whose capabilities are absent.
/// Structural entry/topology capabilities remain validator-owned because safely omitting them
/// would change which models execute.
/// </summary>
internal static class CapabilityDrivenEffectiveRequestProjector
{
    internal static EffectiveClipProjection ProjectUnsupportedFeatures(
        ClipSpec authored,
        VideoArchitectureDescriptor descriptor,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels)
    {
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(stageModels);

        ArchitectureCapabilityDescriptor capabilities =
            ResolvedVideoModelCapabilityPolicy.ForClip(
                authored,
                descriptor,
                stageModels);
        IReadOnlyList<AudioSourceKind> audioSourceKinds =
            ResolvedVideoModelCapabilityPolicy.AudioSourceKindsForClip(
                authored,
                descriptor,
                stageModels);
        HashSet<UnsupportedAuthoringFeature> ignored =
            ArchitectureFeatureVocabulary
                .IgnoredWhenUnsupported(capabilities)
                .ToHashSet();

        List<EffectiveRequestDecision> decisions = [];
        StageSpec[] stages = (authored.Stages ?? []).ToArray();
        ClipSpec effective = authored;

        void Ignore(
            bool configured,
            UnsupportedAuthoringFeature feature,
            string description,
            int? stageId = null,
            int? rawStageIndex = null)
        {
            if (!configured)
            {
                return;
            }
            decisions.Add(EffectiveRequestDecision.Ignore(
                $"effective-request.unsupported-{DiagnosticKey(feature)}-ignored",
                $"Clip {authored.Id}{(stageId.HasValue ? $" Stage {stageId}" : "")} "
                    + $"configures {description}, which {descriptor.DisplayName} does not "
                    + "support. The authored setting remains saved and is ignored for this "
                    + "generation.",
                authored.Id,
                stageId,
                rawStageIndex));
        }

        if (ignored.Contains(UnsupportedAuthoringFeature.FrameReferences))
        {
            Ignore(
                effective.ImageRefs is { Count: > 0 }
                    || stages.Any(stage => stage.ImageRefStrengths is { Count: > 0 }),
                UnsupportedAuthoringFeature.FrameReferences,
                "image/frame references");
            effective = effective with { ImageRefs = [] };
            stages = stages
                .Select(stage => stage with { ImageRefStrengths = [] })
                .ToArray();
        }

        if (ignored.Contains(UnsupportedAuthoringFeature.PromptRelay))
        {
            Ignore(
                effective.PromptWindows is { Count: > 0 },
                UnsupportedAuthoringFeature.PromptRelay,
                "prompt relay windows");
            effective = effective with { PromptWindows = [] };
        }

        if (ignored.Contains(UnsupportedAuthoringFeature.Retake))
        {
            Ignore(
                stages.Any(stage => stage.RetakeWindow is not null),
                UnsupportedAuthoringFeature.Retake,
                "a retake window");
            stages = stages
                .Select(stage => stage with { RetakeWindow = null })
                .ToArray();
        }

        if (ignored.Contains(UnsupportedAuthoringFeature.ReferenceFraming))
        {
            Ignore(
                effective.ReferenceFraming != ReferenceFramingMode.Crop,
                UnsupportedAuthoringFeature.ReferenceFraming,
                "non-default reference framing");
            effective = effective with { ReferenceFraming = ReferenceFramingMode.Crop };
        }

        if (ignored.Contains(UnsupportedAuthoringFeature.StageLoras))
        {
            bool configured = effective.Loras is { Count: > 0 }
                || stages.Any(stage =>
                    stage.Loras is { Count: > 0 }
                    || stage.LoraWeights is { Count: > 0 });
            Ignore(
                configured,
                UnsupportedAuthoringFeature.StageLoras,
                "stage LoRAs");
            effective = effective with { Loras = [] };
            stages = stages
                .Select(stage => stage with { Loras = [], LoraWeights = [] })
                .ToArray();
        }

        if (ignored.Contains(UnsupportedAuthoringFeature.IcLora))
        {
            bool configured = effective.IcLoras is { Count: > 0 }
                || stages.Any(stage =>
                    stage.IcLoraStrengths is { Count: > 0 });
            Ignore(
                configured,
                UnsupportedAuthoringFeature.IcLora,
                "IC-LoRA data");
            effective = effective with { IcLoras = [] };
            stages = stages
                .Select(stage => stage with
                {
                    IcLoraStrengths = [],
                    ControlNetStrength = null,
                })
                .ToArray();
        }
        else if (ignored.Contains(UnsupportedAuthoringFeature.Hdr))
        {
            Ignore(
                effective.IcLoras?.Any(entry => entry.Hdr) == true,
                UnsupportedAuthoringFeature.Hdr,
                "HDR IC-LoRA behavior");
            effective = effective with
            {
                IcLoras = effective.IcLoras?
                    .Select(entry => entry with { Hdr = false })
                    .ToArray(),
            };
        }

        if (ignored.Contains(UnsupportedAuthoringFeature.ClipAudio))
        {
            Ignore(
                effective.UploadedAudio is not null
                    || !string.Equals(
                        effective.AudioSource,
                        Constants.AudioSourceNative,
                        StringComparison.OrdinalIgnoreCase),
                UnsupportedAuthoringFeature.ClipAudio,
                "a clip audio source");
            effective = effective with
            {
                AudioSource = Constants.AudioSourceNative,
                UploadedAudio = null,
            };
        }
        if ((!Has(
                    capabilities.Architecture,
                    ArchitectureCapability.NativeAudio)
                || !audioSourceKinds.Contains(AudioSourceKind.Native))
            && effective.SaveAudioTrack)
        {
            decisions.Add(EffectiveRequestDecision.Ignore(
                "effective-request.unsupported-audio-output-ignored",
                $"Clip {authored.Id} configures standalone audio output, which "
                    + $"{descriptor.DisplayName} does not support. The authored setting remains "
                    + "saved and is ignored for this generation.",
                authored.Id));
            effective = effective with { SaveAudioTrack = false };
        }

        if (ignored.Contains(UnsupportedAuthoringFeature.AudioDerivedDuration))
        {
            Ignore(
                effective.ClipLengthFromAudio,
                UnsupportedAuthoringFeature.AudioDerivedDuration,
                "audio-derived clip duration");
            effective = effective with { ClipLengthFromAudio = false };
        }

        if (ignored.Contains(
                UnsupportedAuthoringFeature.ControlSignalDerivedDuration))
        {
            Ignore(
                effective.ClipLengthFromControlNet,
                UnsupportedAuthoringFeature.ControlSignalDerivedDuration,
                "control-signal-derived clip duration");
            effective = effective with { ClipLengthFromControlNet = false };
        }

        if (ignored.Contains(UnsupportedAuthoringFeature.AudioReuse))
        {
            Ignore(
                effective.ReuseAudio,
                UnsupportedAuthoringFeature.AudioReuse,
                "captured stage audio reuse");
            effective = effective with { ReuseAudio = false };
        }

        if (!Has(
                capabilities.Clip,
                ClipCapability.AudioSegments)
            && effective.BoundaryOutCarryAudio)
        {
            decisions.Add(EffectiveRequestDecision.Ignore(
                "effective-request.unsupported-audio-boundary-ignored",
                $"Clip {authored.Id} configures audio boundary carry, which "
                    + $"{descriptor.DisplayName} does not support. The authored setting remains "
                    + "saved and is ignored for this generation.",
                authored.Id));
            effective = effective with { BoundaryOutCarryAudio = false };
        }

        for (int index = 0; index < stages.Length; index++)
        {
            StageSpec stage = stages[index];
            StageGuideReferenceSelection guide =
                StageGuideReferencePolicy.Classify(stage.ImageReference);
            if (!descriptor.StageGuideReferences.Allows(guide))
            {
                if (guide.Kind == StageGuideReferenceKind.Unknown)
                {
                    // There is no safe semantic fallback for malformed selector syntax.
                    // Preserve it so the post-projection capability validator reports the
                    // existing hard error instead of laundering it into a supported default.
                    stages[index] = stage;
                    continue;
                }
                decisions.Add(EffectiveRequestDecision.Ignore(
                    "effective-request.unsupported-stage-reference-ignored",
                    $"Clip {authored.Id} Stage {stage.Id} uses image selector "
                        + $"'{stage.ImageReference}', which {descriptor.DisplayName} does not "
                        + "support. The authored selector remains saved; this generation uses "
                        + $"'{(index == 0 ? "Generated" : "PreviousStage")}'.",
                    authored.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
                stage = stage with
                {
                    ImageReference =
                        index == 0 ? "Generated" : "PreviousStage",
                };
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
                if (required == StageCapability.None)
                {
                    decisions.Add(EffectiveRequestDecision.Block(
                        "effective-request.unknown-upscale",
                        $"Clip {authored.Id} Stage {stage.Id} uses unknown upscale mode "
                            + $"'{stage.UpscaleMethod}'.",
                        authored.Id,
                        stage.Id,
                        stage.ClipStageRawIndex));
                }
                else if (!Has(
                    ResolvedVideoModelCapabilityPolicy.ForStage(
                        stage,
                        descriptor,
                        stageModels),
                    required))
                {
                    Ignore(
                        configured: true,
                        UnsupportedAuthoringFeature.Upscale,
                        $"unsupported upscale method '{stage.UpscaleMethod}'",
                        stage.Id,
                        stage.ClipStageRawIndex);
                    stage = stage with
                    {
                        Upscale = 1,
                        UpscaleMethod = "pixel-lanczos",
                    };
                }
            }
            stages[index] = stage;
        }

        return new(
            effective with { Stages = Array.AsReadOnly(stages) },
            decisions.AsReadOnly());
    }

    private static string DiagnosticKey(UnsupportedAuthoringFeature feature)
    {
        string key = ArchitectureFeatureVocabulary.AuthoringKey(feature);
        StringBuilder result = new();
        foreach (char value in key)
        {
            if (char.IsUpper(value))
            {
                result.Append('-');
            }
            result.Append(char.ToLowerInvariant(value));
        }
        return result.ToString();
    }

    private static bool Has<T>(T value, T capability) where T : struct, Enum =>
        (Convert.ToInt64(value) & Convert.ToInt64(capability))
            == Convert.ToInt64(capability);
}
