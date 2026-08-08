using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class Ltx2HostIntegrationTests
{
    [Fact]
    public void Ltx_custom_nodes_all_require_the_ltxvideo_feature()
    {
        // Set equality, not a spot check: a node registered without the flag and a new
        // registration missing from this list both have to fail.
        Assert.Equal(
            [
                LTXAddVideoICLoRAGuideNode.ClassType,
                LTXAddVideoICLoRAGuideAdvancedNode.ClassType,
                LTXICLoRALoaderModelOnlyNode.ClassType,
                LTXVSetAudioRefTokensNode.ClassType,
                LTXVSetAudioVideoMaskByTimeNode.ClassType,
                LTXVSetVideoLatentNoiseMasksNode.ClassType,
            ],
            ComfyUIBackendExtension.NodeToFeatureMap
                .Where(entry => entry.Value == Ltx2HostIntegration.FeatureFlag)
                .Select(entry => entry.Key)
                .OrderBy(nodeClass => nodeClass, StringComparer.Ordinal)
                .ToArray());
    }
}
