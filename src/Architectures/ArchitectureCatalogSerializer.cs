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
        ["clip"] = new JArray(ArchitectureFeatureVocabulary.WireNames(descriptor.Capabilities.Clip)),
        ["stage"] = new JArray(ArchitectureFeatureVocabulary.WireNames(descriptor.Capabilities.Stage)),
        ["upscaleModes"] = new JArray(ArchitectureFeatureVocabulary.UpscaleModeWireNames(descriptor.Capabilities.Stage)),
        // Exact inputs that are not representable as flag sets.
        ["entryModes"] = new JArray(descriptor.EntryModes.Select(ArchitectureFeatureVocabulary.WireName)),
        ["audioSourceKinds"] = new JArray(descriptor.AudioSourceKinds.Select(
            SerializeAudioSourceKind)),
    };

    private static JObject SerializeRule(RuleDecision decision) => new()
    {
        ["support"] = SerializeRuleSupport(decision.Support),
        ["code"] = decision.Code,
        ["reason"] = decision.Reason,
        ["scope"] = SerializeRuleScope(decision.Scope),
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
            },
            MinimumStageControlRuleConstraints value => new()
            {
                ["exclusiveMinimumControl"] = value.ExclusiveMinimumControl,
            },
            MutuallyExclusiveRuleConstraints value => new()
            {
                ["mutuallyExclusive"] = new JArray(
                    value.MutuallyExclusive.Select(ArchitectureFeatureVocabulary.AuthoringKey)),
            },
            RequiredEntryModesRuleConstraints value => new()
            {
                ["requiresAnyEntryMode"] = new JArray(
                    value.RequiresAnyEntryMode.Select(ArchitectureFeatureVocabulary.WireName)),
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
            ["clip"] = new JArray(ArchitectureFeatureVocabulary.WireNames(capabilities.Clip)),
            ["stage"] = new JArray(ArchitectureFeatureVocabulary.WireNames(capabilities.Stage)),
            ["upscaleModes"] = new JArray(ArchitectureFeatureVocabulary.UpscaleModeWireNames(capabilities.Stage)),
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
                yield return ArchitectureFeatureVocabulary.WireName(mode);
            }
        }
    }

    private static string SerializeAudioSourceKind(AudioSourceKind kind) => kind switch
    {
        AudioSourceKind.Disabled => "Disabled",
        AudioSourceKind.Native => "Native",
        AudioSourceKind.Upload => "Upload",
        AudioSourceKind.ControlNet => "ControlNet",
        AudioSourceKind.AceStepFun => "AceStepFun",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string SerializeBoundaryMode(BoundaryJoinType mode) => mode switch
    {
        BoundaryJoinType.Cut => "cut",
        BoundaryJoinType.Continue => "continue",
        BoundaryJoinType.Crossfade => "crossfade",
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
