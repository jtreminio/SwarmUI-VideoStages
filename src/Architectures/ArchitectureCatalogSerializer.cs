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
        ["schemaVersion"] = 2,
        ["architectures"] = new JArray(registry.Catalog.Select(Serialize)),
        ["models"] = new JArray(registry.ResolvedModels.Select(Serialize)),
    };

    private static JObject Serialize(VideoArchitectureDescriptor descriptor) => new()
    {
        ["id"] = descriptor.Id.Value,
        ["label"] = descriptor.DisplayName,
        ["capabilities"] = SerializeCapabilities(descriptor),
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
            MinimumStageControlRuleConstraints value => new()
            {
                ["exclusiveMinimumControl"] = value.ExclusiveMinimumControl,
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

    private static JObject Serialize(ResolvedVideoModel model) => new()
    {
        ["modelName"] = model.ModelName,
        ["architectureId"] = model.ArchitectureId.Value,
        ["modelProfileId"] = model.ModelProfileId.Value,
        ["modelClassId"] = model.ModelClassId,
        ["compatibilityClassId"] = model.CompatibilityClassId,
        ["frameGrid"] = model.FrameGrid,
        ["entryAbilities"] = new JArray(ModelEntryAbilities(model.EntryAbilities)),
        ["capabilities"] = SerializeCapabilities(model),
        ["enhancements"] = new JObject
        {
            ["referencePositions"] = new JArray(model.ReferencePositions ?? []),
        },
    };

    private static JObject SerializeCapabilities(ResolvedVideoModel model)
    {
        ArchitectureCapabilityDescriptor capabilities = model.Architecture.Capabilities;
        IReadOnlyList<AudioSourceKind> audioKinds = model.Architecture.AudioSourceKinds;
        return new()
        {
            ["architecture"] = new JArray(
                ArchitectureCapabilities(capabilities.Architecture)),
            ["clip"] = new JArray(ClipCapabilities(capabilities.Clip)),
            ["stage"] = new JArray(StageCapabilities(capabilities.Stage)),
            ["upscaleModes"] = new JArray(UpscaleModes(capabilities.Stage)),
            ["entryModes"] = new JArray(ModelEntryModes(model)),
            ["audioSourceKinds"] = new JArray(audioKinds.Select(SerializeAudioSourceKind)),
        };
    }

    private static IEnumerable<string> ModelEntryAbilities(VideoModelEntryAbility abilities)
    {
        if (Has(abilities, VideoModelEntryAbility.TextToVideo))
        {
            yield return "text";
        }
        if (Has(abilities, VideoModelEntryAbility.ImageToVideo))
        {
            yield return "image";
        }
    }

    private static IEnumerable<string> ModelEntryModes(ResolvedVideoModel model)
    {
        foreach (ArchitectureEntryMode mode in model.Architecture.EntryModes)
        {
            VideoModelEntryAbility required = mode == ArchitectureEntryMode.TextToVideo
                ? VideoModelEntryAbility.TextToVideo
                : VideoModelEntryAbility.ImageToVideo;
            if ((model.EntryAbilities & required) == required)
            {
                yield return SerializeEntryMode(mode);
            }
        }
    }

    private static IEnumerable<string> ArchitectureCapabilities(ArchitectureCapability value)
        => ArchitectureFeatureVocabulary.WireNames(value);

    private static IEnumerable<string> ClipCapabilities(ClipCapability value)
        => ArchitectureFeatureVocabulary.WireNames(value);

    private static IEnumerable<string> StageCapabilities(StageCapability value)
        => ArchitectureFeatureVocabulary.WireNames(value);

    private static IEnumerable<string> UpscaleModes(StageCapability value)
        => ArchitectureFeatureVocabulary.UpscaleModeWireNames(value);

    private static string SerializeEntryMode(ArchitectureEntryMode mode) => mode switch
    {
        ArchitectureEntryMode.TextToVideo => "text-to-video",
        ArchitectureEntryMode.ImageToVideo => "image-to-video",
        ArchitectureEntryMode.SourceVideo => "source-video",
        ArchitectureEntryMode.RefineVideo => "refine-video",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string SerializeConditionalFeature(ConditionalRuleFeature feature) =>
        ArchitectureFeatureVocabulary.AuthoringKey(feature);

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
        RuleScope.Clip => "clip",
        RuleScope.Stage => "stage",
        RuleScope.Boundary => "boundary",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static bool Has<T>(T value, T flags) where T : struct, Enum
    {
        long raw = Convert.ToInt64(value);
        long requested = Convert.ToInt64(flags);
        return (raw & requested) == requested;
    }
}
