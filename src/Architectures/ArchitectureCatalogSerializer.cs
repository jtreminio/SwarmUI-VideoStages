using Newtonsoft.Json.Linq;
using VideoStages.Architectures.Abstractions;

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
                ArchitectureFeatureVocabulary.WireName(pair.Key),
                SerializeRule(pair.Value)))),
    };

    private static JObject SerializeCapabilities(VideoArchitectureDescriptor descriptor) => new()
    {
        ["features"] = new JArray(ArchitectureFeatureVocabulary.WireNames(descriptor.Features)),
        // Exact inputs that are not representable as flag sets.
        ["entryModes"] = new JArray(descriptor.EntryModes.Select(ArchitectureFeatureVocabulary.WireName)),
        ["audioSourceKinds"] = new JArray(descriptor.AudioSourceKinds.Select(
            ArchitectureFeatureVocabulary.WireName)),
    };

    private static JObject SerializeRule(RuleDecision decision) => new()
    {
        ["support"] = ArchitectureFeatureVocabulary.WireName(decision.Support),
        ["code"] = decision.Code,
        ["reason"] = decision.Reason,
        ["constraints"] = decision.Constraints is null
            ? null
            : SerializeRuleConstraints(decision.Constraints),
    };

    private static JObject SerializeRuleConstraints(BoundaryRuleConstraints value) => new()
    {
        ["sameArchitecture"] = true,
        ["frameStep"] = value.FrameStep,
        ["minFrames"] = value.MinFrames,
        ["maxFrames"] = value.MaxFrames,
        ["defaultFrames"] = value.DefaultFrames,
        ["continuityExtraFrames"] = value.ContinuityExtraFrames,
        ["continueMode"] = ArchitectureFeatureVocabulary.WireName(value.ContinueMode),
        ["targetRequiresGeneratedEntry"] = value.TargetRequiresGeneratedEntry,
        ["targetRequiresStage"] = value.TargetRequiresStage,
        ["targetDisallowsInitialReference"] = value.TargetDisallowsInitialReference,
    };

    private static JObject Serialize(ResolvedVideoModel model) => new()
    {
        ["modelName"] = model.ModelName,
        ["architectureId"] = model.ArchitectureId.Value,
        ["modelProfileId"] = model.ModelProfileId.Value,
        ["modelClassId"] = model.ModelClassId,
        ["compatibilityClassId"] = model.CompatibilityClassId,
        ["frameGrid"] = model.FrameGrid,
        ["frameGridOrigin"] = model.FrameGridOrigin,
        ["capabilities"] = SerializeCapabilities(model.Architecture),
        ["enhancements"] = new JObject
        {
            ["referencePositions"] = new JArray(
                (model.FrameReferencePositions ?? [])
                    .Select(ArchitectureFeatureVocabulary.WireName)),
        },
    };

}
