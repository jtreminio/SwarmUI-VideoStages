using Newtonsoft.Json.Linq;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>
/// Stable wire projection for the architecture catalog. Domain contracts remain free of JSON
/// concerns, and every enum is mapped explicitly so renaming a C# symbol cannot silently change
/// the frontend contract.
/// </summary>
internal static class ArchitectureCatalogSerializer
{
    internal static JObject Serialize(IVideoArchitectureRegistry registry) => new()
    {
        ["architectures"] = new JArray(registry.Catalog.Select(Serialize)),
        ["models"] = new JArray(registry.ResolvedModels.Select(Serialize)),
    };

    private static JObject Serialize(VideoArchitectureDescriptor descriptor) => new()
    {
        ["id"] = descriptor.Id.Value,
        ["label"] = descriptor.DisplayName,
        ["defaultProfileId"] = descriptor.DefaultProfileId.Value,
        ["capabilities"] = SerializeCapabilities(descriptor),
        ["profiles"] = new JArray(descriptor.Profiles.Select(Serialize)),
        ["boundaryRules"] = new JObject(descriptor.BoundaryRules.Select(pair =>
            new JProperty(
                SerializeBoundaryMode(pair.Key),
                SerializeRule(pair.Value)))),
        ["rules"] = new JArray(descriptor.Rules.Select(SerializeRule)),
    };

    private static JObject SerializeCapabilities(VideoArchitectureDescriptor descriptor) => new()
    {
        // Authoritative, scope-preserving capability sets.
        ["architecture"] = new JArray(ArchitectureCapabilities(descriptor.Capabilities.Architecture)),
        ["clip"] = new JArray(ClipCapabilities(descriptor.Capabilities.Clip)),
        ["stage"] = new JArray(StageCapabilities(descriptor.Capabilities.Stage)),
        ["output"] = new JArray(OutputCapabilities(descriptor.Capabilities.Output)),
        ["upscaleModes"] = new JArray(UpscaleModes(descriptor.Capabilities.Stage)),

        // Exact inputs that are not representable as flag sets.
        ["entryModes"] = new JArray(descriptor.EntryModes.Select(SerializeEntryMode)),
        ["audioSourceKinds"] = new JArray(descriptor.AudioSourceKinds.Select(
            SerializeAudioSourceKind)),
    };

    private static JObject SerializeRule(RuleDecision decision) => new()
    {
        ["support"] = SerializeRuleSupport(decision.Support),
        ["code"] = decision.Code,
        ["reason"] = decision.Reason,
        ["scope"] = SerializeRuleScope(decision.Scope),
        ["entityId"] = decision.EntityId,
        ["constraints"] = decision.Constraints is null
            ? null
            : SerializeRuleConstraints(decision.Constraints),
    };

    private static JObject SerializeRuleConstraints(RuleConstraints constraints) =>
        constraints switch
        {
            BoundaryRuleConstraints value => new()
            {
                ["sameArchitecture"] = true,
                ["frameStep"] = value.FrameStep,
                ["minFrames"] = value.MinFrames,
                ["maxFrames"] = value.MaxFrames,
                ["defaultFrames"] = value.DefaultFrames,
                ["continuityExtraFrames"] = value.ContinuityExtraFrames,
                ["targetRequiresGeneratedEntry"] = value.TargetRequiresGeneratedEntry,
                ["targetRequiresStage"] = value.TargetRequiresStage,
                ["targetDisallowsInitialReference"] = value.TargetDisallowsInitialReference,
            },
            MinimumActiveStagesRuleConstraints value => new()
            {
                ["minimumActiveStages"] = value.MinimumActiveStages,
                ["failureSeverity"] = SerializeFailureSeverity(value.FailureSeverity),
                ["failureEffect"] = SerializeFailureEffect(value.FailureEffect),
            },
            FixedFrameCountRuleConstraints value => new()
            {
                ["requiresFixedFrameCount"] = value.RequiresFixedFrameCount,
            },
            MutuallyExclusiveRuleConstraints value => new()
            {
                ["mutuallyExclusive"] = new JArray(
                    value.MutuallyExclusive.Select(SerializeConditionalFeature)),
            },
            RequiredEntryModesRuleConstraints value => new()
            {
                ["requiresAnyEntryMode"] = new JArray(
                    value.RequiresAnyEntryMode.Select(SerializeEntryMode)),
            },
            UniformTimelineFeatureRuleConstraints value => new()
            {
                ["uniformTimelineFeature"] =
                    SerializeConditionalFeature(value.UniformTimelineFeature),
                ["minimumTimelineClips"] = value.MinimumTimelineClips,
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(constraints),
                constraints,
                "Unknown architecture rule constraint type."),
        };

    private static JObject Serialize(VideoModelProfileDescriptor profile) => new()
    {
        ["id"] = profile.Id.Value,
        ["label"] = profile.DisplayName,
        ["capabilities"] = new JArray(ProfileCapabilities(profile.Capabilities)),
        ["rules"] = new JArray(profile.Rules.Select(SerializeRule)),
    };

    private static JObject Serialize(ResolvedVideoModel model) => new()
    {
        ["modelName"] = model.ModelName,
        ["architectureId"] = model.ArchitectureId.Value,
        ["modelProfileId"] = model.ModelProfileId.Value,
        ["compatId"] = model.HostCompatibilityId,
    };

    private static IEnumerable<string> ArchitectureCapabilities(ArchitectureCapability value)
    {
        if (Has(value, ArchitectureCapability.GeneratedEntry))
        {
            yield return "generated-entry";
        }
        if (Has(value, ArchitectureCapability.SourcedEntry))
        {
            yield return "sourced-entry";
        }
        if (Has(value, ArchitectureCapability.MultiStage))
        {
            yield return "multi-stage";
        }
        if (Has(value, ArchitectureCapability.NativeAudio))
        {
            yield return "native-audio";
        }
        if (Has(value, ArchitectureCapability.DecodedOutput))
        {
            yield return "decoded-output";
        }
    }

    private static IEnumerable<string> ClipCapabilities(ClipCapability value)
    {
        if (Has(value, ClipCapability.SourceVideo))
        {
            yield return "source-video";
        }
        if (Has(value, ClipCapability.Prompts))
        {
            yield return "prompts";
        }
        if (Has(value, ClipCapability.PromptRelay))
        {
            yield return "prompt-relay";
        }
        if (Has(value, ClipCapability.References))
        {
            yield return "references";
        }
        if (Has(value, ClipCapability.ReferenceFraming))
        {
            yield return "reference-framing";
        }
        if (Has(value, ClipCapability.Retake))
        {
            yield return "retake";
        }
        if (Has(value, ClipCapability.AudioSources))
        {
            yield return "audio-sources";
        }
        if (Has(value, ClipCapability.AudioSegments))
        {
            yield return "audio-segments";
        }
    }

    private static IEnumerable<string> StageCapabilities(StageCapability value)
    {
        if (Has(value, StageCapability.ImageInput))
        {
            yield return "image-input";
        }
        if (Has(value, StageCapability.VideoInput))
        {
            yield return "video-input";
        }
        if (Has(value, StageCapability.PixelUpscale))
        {
            yield return "pixel-upscale";
        }
        if (Has(value, StageCapability.ModelUpscale))
        {
            yield return "model-upscale";
        }
        if (Has(value, StageCapability.LatentUpscale))
        {
            yield return "latent-upscale";
        }
        if (Has(value, StageCapability.LatentModelUpscale))
        {
            yield return "latent-model-upscale";
        }
        if (Has(value, StageCapability.Lora))
        {
            yield return "lora";
        }
        if (Has(value, StageCapability.IcLora))
        {
            yield return "ic-lora";
        }
        if (Has(value, StageCapability.Hdr))
        {
            yield return "hdr";
        }
        if (Has(value, StageCapability.FrameReferences))
        {
            yield return "frame-references";
        }
    }

    private static IEnumerable<string> OutputCapabilities(OutputCapability value)
    {
        if (Has(value, OutputCapability.Video))
        {
            yield return "video";
        }
        if (Has(value, OutputCapability.AttachedAudio))
        {
            yield return "attached-audio";
        }
        if (Has(value, OutputCapability.StandaloneAudio))
        {
            yield return "standalone-audio";
        }
    }

    private static IEnumerable<string> UpscaleModes(StageCapability value)
    {
        if (Has(value, StageCapability.PixelUpscale))
        {
            yield return "pixel";
        }
        if (Has(value, StageCapability.ModelUpscale))
        {
            yield return "model";
        }
        if (Has(value, StageCapability.LatentUpscale))
        {
            yield return "latent";
        }
        if (Has(value, StageCapability.LatentModelUpscale))
        {
            yield return "latent-model";
        }
    }

    private static IEnumerable<string> ProfileCapabilities(ModelProfileCapability value)
    {
        if (Has(value, ModelProfileCapability.SamplerSelection))
        {
            yield return "sampler-selection";
        }
        if (Has(value, ModelProfileCapability.SchedulerSelection))
        {
            yield return "scheduler-selection";
        }
        if (Has(value, ModelProfileCapability.DimensionRules))
        {
            yield return "dimension-rules";
        }
        if (Has(value, ModelProfileCapability.FrameRules))
        {
            yield return "frame-rules";
        }
        if (Has(value, ModelProfileCapability.NormalLora))
        {
            yield return "normal-lora";
        }
    }

    private static string SerializeEntryMode(ArchitectureEntryMode mode) => mode switch
    {
        ArchitectureEntryMode.TextToVideo => "text-to-video",
        ArchitectureEntryMode.ImageToVideo => "image-to-video",
        ArchitectureEntryMode.SourceVideo => "source-video",
        ArchitectureEntryMode.RefineVideo => "refine-video",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string SerializeConditionalFeature(ConditionalRuleFeature feature) =>
        feature switch
        {
            ConditionalRuleFeature.Retake => "retake",
            ConditionalRuleFeature.FrameReferences => "frameReferences",
            ConditionalRuleFeature.Hdr => "hdr",
            _ => throw new ArgumentOutOfRangeException(nameof(feature)),
        };

    private static string SerializeFailureSeverity(RuleFailureSeverity severity) =>
        severity switch
        {
            RuleFailureSeverity.Warning => "warning",
            _ => throw new ArgumentOutOfRangeException(nameof(severity)),
        };

    private static string SerializeFailureEffect(RuleFailureEffect effect) => effect switch
    {
        RuleFailureEffect.DisableFeature => "disable-feature",
        _ => throw new ArgumentOutOfRangeException(nameof(effect)),
    };

    private static string SerializeAudioSourceKind(AudioSourceKind kind) => kind switch
    {
        AudioSourceKind.Disabled => "Disabled",
        AudioSourceKind.Native => "Native",
        AudioSourceKind.Upload => "Upload",
        AudioSourceKind.ControlNet => "ControlNet",
        AudioSourceKind.AceStepFun => "AceStepFun",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string SerializeBoundaryMode(BoundaryExecutionMode mode) => mode switch
    {
        BoundaryExecutionMode.Cut => "cut",
        BoundaryExecutionMode.Continue => "continue",
        BoundaryExecutionMode.Crossfade => "crossfade",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string SerializeRuleSupport(RuleSupport support) => support switch
    {
        RuleSupport.Supported => "supported",
        RuleSupport.Unsupported => "unsupported",
        RuleSupport.Conditional => "conditional",
        _ => throw new ArgumentOutOfRangeException(nameof(support)),
    };

    private static string SerializeRuleScope(RuleScope scope) => scope switch
    {
        RuleScope.Architecture => "architecture",
        RuleScope.ModelProfile => "model-profile",
        RuleScope.Clip => "clip",
        RuleScope.Stage => "stage",
        RuleScope.Boundary => "boundary",
        RuleScope.Output => "output",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static bool Has<T>(T value, T flags) where T : struct, Enum
    {
        long raw = Convert.ToInt64(value);
        long requested = Convert.ToInt64(flags);
        return (raw & requested) == requested;
    }
}
