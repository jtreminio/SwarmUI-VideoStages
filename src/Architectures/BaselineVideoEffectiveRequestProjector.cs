using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>The effective clip and typed dispositions produced by a pure projection.</summary>
internal sealed record EffectiveClipProjection(
    ClipSpec Clip,
    IReadOnlyList<EffectiveRequestDecision> Decisions);

/// <summary>
/// Shared graph-free projection for the proven WAN/generic-host baseline.
/// Architecture owners choose the policy inputs and remain responsible for
/// architecture-private additions such as WAN bounded frame references.
/// </summary>
internal static class BaselineVideoEffectiveRequestProjector
{
    internal static EffectiveClipProjection ProjectUnsupportedEnhancements(
        ClipSpec effective,
        string architectureName,
        string codePrefix)
    {
        ArgumentNullException.ThrowIfNull(effective);

        List<EffectiveRequestDecision> decisions = [];
        bool hasIcLoraData = effective.IcLoras is { Count: > 0 }
            || effective.Stages?.Any(
                stage => stage.IcLoraStrengths is { Count: > 0 }) == true;
        if (hasIcLoraData)
        {
            decisions.Add(EffectiveRequestDecision.Ignore(
                $"effective-request.{codePrefix}-ic-lora-ignored",
                $"Clip {effective.Id} configures IC-LoRA data, but {architectureName} does "
                    + "not support IC-LoRA. The setting remains authored and is ignored for "
                    + "this generation.",
                effective.Id));
            effective = effective with
            {
                IcLoras = [],
                Stages = effective.Stages?
                    .Select(stage => stage with { IcLoraStrengths = [] })
                    .ToArray(),
            };
        }

        if (effective.Stages is not { Count: > 0 })
        {
            return new(effective, decisions.AsReadOnly());
        }

        StageSpec[] stages = effective.Stages.ToArray();
        for (int index = 0; index < stages.Length; index++)
        {
            StageSpec stage = stages[index];
            if (stage.Upscale == 1)
            {
                continue;
            }
            if (stage.IsPixelUpscale)
            {
                decisions.Add(EffectiveRequestDecision.Execute(
                    $"effective-request.{codePrefix}-pixel-upscale",
                    $"Clip {effective.Id} Stage {stage.Id} executes its pixel upscale.",
                    effective.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
                continue;
            }
            if (!stage.IsModelUpscale
                && !stage.IsLatentUpscale
                && !stage.IsLatentModelUpscale)
            {
                decisions.Add(EffectiveRequestDecision.Block(
                    "effective-request.unknown-upscale",
                    $"Clip {effective.Id} Stage {stage.Id} uses unknown upscale mode "
                        + $"'{stage.UpscaleMethod}'.",
                    effective.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
                continue;
            }
            decisions.Add(EffectiveRequestDecision.Ignore(
                $"effective-request.{codePrefix}-advanced-upscale-ignored",
                $"Clip {effective.Id} Stage {stage.Id} requests unsupported "
                    + $"{architectureName} upscale method '{stage.UpscaleMethod}'. The authored "
                    + "setting remains saved, but this generation runs the stage at 1×.",
                effective.Id,
                stage.Id,
                stage.ClipStageRawIndex));
            stages[index] = stage with
            {
                Upscale = 1,
                UpscaleMethod = "pixel-lanczos",
            };
        }
        return new(
            effective with { Stages = Array.AsReadOnly(stages) },
            decisions.AsReadOnly());
    }

    internal static EffectiveClipProjection ProjectBaseline(
        ClipSpec effective,
        bool preserveFrameReferences,
        VideoArchitectureDescriptor descriptor,
        string architectureName,
        string codePrefix)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(descriptor);

        List<EffectiveRequestDecision> decisions = [];
        void IgnoreConfigured(bool configured, string feature, string code)
        {
            if (!configured)
            {
                return;
            }
            decisions.Add(EffectiveRequestDecision.Ignore(
                $"effective-request.{codePrefix}-{code}-ignored",
                $"Clip {effective.Id} configures {feature}, which {architectureName} does not "
                    + "use. The authored setting remains saved and is ignored for this "
                    + "generation.",
                effective.Id));
        }

        if (!preserveFrameReferences)
        {
            IgnoreConfigured(
                effective.ImageRefs is { Count: > 0 }
                    || effective.Stages?.Any(
                        stage => stage.ImageRefStrengths is { Count: > 0 }) == true,
                "image/frame references",
                "references");
        }
        else
        {
            IgnoreConfigured(
                effective.Stages?.Any(
                    stage => stage.ImageRefStrengths?.Any(
                        strength => Math.Abs(
                            strength - Constants.DefaultStageRefStrength)
                            > 0.000001) == true) == true,
                "per-stage frame-reference strengths",
                "reference-strengths");
        }
        IgnoreConfigured(
            effective.PromptWindows is { Count: > 0 },
            "prompt relay windows",
            "prompt-relay");
        IgnoreConfigured(
            effective.Stages?.Any(stage => stage.RetakeWindow is not null) == true,
            "a retake window",
            "retake");
        bool hasIcLoraConfiguration =
            effective.IcLoras is { Count: > 0 }
            || effective.Stages?.Any(
                stage => stage.IcLoraStrengths is { Count: > 0 }) == true;
        IgnoreConfigured(
            hasIcLoraConfiguration
                && effective.Stages?.Any(
                    stage => stage.ControlNetStrength.HasValue) == true,
            "stage ControlNet strength used by IC-LoRA",
            "controlnet");
        IgnoreConfigured(
            effective.UploadedAudio is not null
                || !string.Equals(
                    effective.AudioSource,
                    Constants.AudioSourceNative,
                    StringComparison.OrdinalIgnoreCase),
            "a clip audio source",
            "audio-source");
        IgnoreConfigured(effective.SaveAudioTrack, "standalone audio output", "audio-output");
        IgnoreConfigured(
            effective.ClipLengthFromAudio,
            "audio-derived clip duration",
            "audio-duration");
        IgnoreConfigured(
            effective.ClipLengthFromControlNet,
            "control-derived clip duration",
            "control-duration");
        IgnoreConfigured(effective.ReuseAudio, "captured stage audio reuse", "audio-reuse");
        IgnoreConfigured(
            effective.BoundaryOutCarryAudio,
            "audio boundary carry",
            "audio-boundary");
        IgnoreConfigured(
            effective.ReferenceFraming != ReferenceFramingMode.Crop,
            "non-default reference framing",
            "reference-framing");

        StageSpec[] stages = (effective.Stages ?? [])
            .Select((stage, index) =>
            {
                StageGuideReferenceSelection guide =
                    StageGuideReferencePolicy.Classify(stage.ImageReference);
                bool supportedGuide = descriptor.StageGuideReferences.Allows(guide);
                if (!supportedGuide)
                {
                    decisions.Add(EffectiveRequestDecision.Ignore(
                        $"effective-request.{codePrefix}-stage-reference-ignored",
                        $"Clip {effective.Id} Stage {stage.Id} uses image selector "
                            + $"'{stage.ImageReference}', which {architectureName} does not use. "
                            + "The authored selector remains saved; this generation uses "
                            + $"'{(index == 0 ? "Generated" : "PreviousStage")}'.",
                        effective.Id,
                        stage.Id,
                        stage.ClipStageRawIndex));
                }
                return stage with
                {
                    ImageReference = supportedGuide
                        ? stage.ImageReference
                        : index == 0 ? "Generated" : "PreviousStage",
                    ImageRefStrengths = [],
                    RetakeWindow = null,
                    ControlNetStrength = null,
                };
            })
            .ToArray();
        return new(
            effective with
            {
                AudioSource = Constants.AudioSourceNative,
                SaveAudioTrack = false,
                ClipLengthFromAudio = false,
                ClipLengthFromControlNet = false,
                ReuseAudio = false,
                UploadedAudio = null,
                ImageRefs = preserveFrameReferences ? effective.ImageRefs : [],
                PromptWindows = [],
                BoundaryOutCarryAudio = false,
                ReferenceFraming = ReferenceFramingMode.Crop,
                Stages = Array.AsReadOnly(stages),
            },
            decisions.AsReadOnly());
    }
}
