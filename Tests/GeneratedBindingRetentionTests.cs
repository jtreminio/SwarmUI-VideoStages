using ComfyTyped.Core;
using VideoStages.Generated;
using Xunit;

namespace VideoStages.Tests;

public class GeneratedBindingRetentionTests
{
    // These bindings are outside the keep manifest because production C# under src/ references
    // them directly. Keep this audit list disjoint from AlwaysKeep so a new generated wrapper must
    // have an explicit retention reason.
    private static readonly string[] DirectlyReferencedProductionBindings =
    [
        nameof(SwarmAudioLengthToFramesNode),
        nameof(SwarmFrameWindowNode),
        nameof(SwarmPromptRelayEncodeNode),
        nameof(SwarmRampMaskBatchNode),
        nameof(SwarmSetAudioMaskWindowsNode),
    ];

    [Fact]
    public void EveryGeneratedNodeBindingHasExplicitPruneRetentionReason()
    {
        HashSet<string> generatedNodeTypes = typeof(PruneManifest).Assembly
            .GetTypes()
            .Where(type =>
                type.Namespace == typeof(PruneManifest).Namespace
                && !type.IsAbstract
                && typeof(ComfyNode).IsAssignableFrom(type))
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            PruneManifest.AlwaysKeep.Length,
            PruneManifest.AlwaysKeep.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(PruneManifest.AlwaysKeep.Except(
            generatedNodeTypes,
            StringComparer.Ordinal));
        Assert.Equal(
            DirectlyReferencedProductionBindings.Length,
            DirectlyReferencedProductionBindings
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Empty(DirectlyReferencedProductionBindings.Intersect(
            PruneManifest.AlwaysKeep,
            StringComparer.Ordinal));
        Assert.Empty(DirectlyReferencedProductionBindings.Except(
            generatedNodeTypes,
            StringComparer.Ordinal));
        Assert.Empty(generatedNodeTypes.Except(
            PruneManifest.AlwaysKeep.Concat(DirectlyReferencedProductionBindings),
            StringComparer.Ordinal));
        Assert.Contains(nameof(SwarmFrameImageNode), PruneManifest.AlwaysKeep);
    }
}
