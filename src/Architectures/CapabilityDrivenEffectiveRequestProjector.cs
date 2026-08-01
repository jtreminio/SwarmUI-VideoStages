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
        VideoArchitectureDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(descriptor);
        IReadOnlyList<AudioSourceKind> audioSourceKinds = descriptor.AudioSourceKinds;
        bool Unsupported(ArchitectureFeature feature) =>
            !descriptor.Features.HasFlag(feature);

        List<EffectiveRequestDecision> decisions = [];
        StageSpec[] stages = (authored.Stages ?? []).ToArray();
        ClipSpec effective = authored;

        void Ignore(
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

        if (Unsupported(ArchitectureFeature.FrameReferences))
        {
            Ignore(
                effective.ImageRefs is { Count: > 0 }
                    || stages.Any(stage => stage.ImageRefStrengths is { Count: > 0 }),
                ArchitectureFeature.FrameReferences,
                "image/frame references");
            effective = effective with { ImageRefs = [] };
            stages = stages
                .Select(stage => stage with { ImageRefStrengths = [] })
                .ToArray();
        }

        if (Unsupported(ArchitectureFeature.PromptRelay))
        {
            Ignore(
                effective.PromptWindows is { Count: > 0 },
                ArchitectureFeature.PromptRelay,
                "prompt relay windows");
            effective = effective with { PromptWindows = [] };
        }

        if (Unsupported(ArchitectureFeature.Retake))
        {
            Ignore(
                stages.Any(stage => stage.RetakeWindow is not null),
                ArchitectureFeature.Retake,
                "a retake window");
            stages = stages
                .Select(stage => stage with { RetakeWindow = null })
                .ToArray();
        }

        if (Unsupported(ArchitectureFeature.ReferenceFraming))
        {
            Ignore(
                effective.ReferenceFraming != ReferenceFramingMode.Crop,
                ArchitectureFeature.ReferenceFraming,
                "non-default reference framing");
            effective = effective with { ReferenceFraming = ReferenceFramingMode.Crop };
        }

        if (Unsupported(ArchitectureFeature.IcLora))
        {
            bool configured = effective.IcLoras is { Count: > 0 }
                || stages.Any(stage =>
                    stage.IcLoraStrengths is { Count: > 0 });
            Ignore(
                configured,
                ArchitectureFeature.IcLora,
                "IC-LoRA data");
            // The control signal is IC-LoRA media, so its duration goes with it.
            Ignore(
                effective.ClipLengthFromControlNet,
                ArchitectureFeature.IcLora,
                "control-signal-derived clip duration");
            effective = effective with
            {
                IcLoras = [],
                ClipLengthFromControlNet = false,
            };
            stages = stages
                .Select(stage => stage with
                {
                    IcLoraStrengths = [],
                    ControlNetStrength = null,
                })
                .ToArray();
        }

        AudioSourceKind authoredAudioKind =
            AudioSourceParser.Parse(effective.AudioSource).Kind;
        if (authoredAudioKind != AudioSourceKind.Unknown
            && !audioSourceKinds.Contains(authoredAudioKind)
            && !(authoredAudioKind == AudioSourceKind.Native
                && audioSourceKinds.Contains(AudioSourceKind.Disabled)))
        {
            decisions.Add(EffectiveRequestDecision.Ignore(
                "effective-request.unsupported-audio-source-ignored",
                $"Clip {authored.Id} configures audio source kind '{authoredAudioKind}', "
                    + $"which {descriptor.DisplayName} does not support. The authored setting "
                    + "remains saved and is ignored for this generation.",
                authored.Id));
            effective = effective with
            {
                AudioSource = Constants.AudioSourceNative,
                UploadedAudio = null,
            };
        }

        if (!audioSourceKinds.Contains(AudioSourceKind.Native)
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

        if (Unsupported(ArchitectureFeature.AudioDerivedDuration))
        {
            Ignore(
                effective.ClipLengthFromAudio,
                ArchitectureFeature.AudioDerivedDuration,
                "audio-derived clip duration");
            effective = effective with { ClipLengthFromAudio = false };
        }

        if (Unsupported(ArchitectureFeature.AudioReuse))
        {
            Ignore(
                effective.ReuseAudio,
                ArchitectureFeature.AudioReuse,
                "captured stage audio reuse");
            effective = effective with { ReuseAudio = false };
        }

        if (Unsupported(ArchitectureFeature.AudioSegments)
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

            // Every architecture can drive every known upscale method, so only an
            // unrecognized one is refused.
            if (stage.Upscale != 1
                && StageUpscalePlanCompiler.Classify(stage.UpscaleMethod)
                    == StageUpscaleMode.Unsupported)
            {
                decisions.Add(EffectiveRequestDecision.Block(
                    "effective-request.unknown-upscale",
                    $"Clip {authored.Id} Stage {stage.Id} uses unknown upscale mode "
                        + $"'{stage.UpscaleMethod}'.",
                    authored.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            stages[index] = stage;
        }

        return new(
            effective with { Stages = Array.AsReadOnly(stages) },
            decisions.AsReadOnly());
    }

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
