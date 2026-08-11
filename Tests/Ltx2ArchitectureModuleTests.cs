using VideoStages.Architectures.Abstractions;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class Ltx2ArchitectureModuleTests
{
    [Theory]
    [InlineData("lightricks-ltx-video-2-3", "ltx-2.3")]
    [InlineData("lightricks-ltx-video-2-5", "ltx-2.5")]
    public void Supported_versions_share_the_LTX_2_feature_contract(
        string modelClassId,
        string expectedProfileId)
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = modelClassId,
        };

        Assert.True(Ltx2ArchitectureModule.Instance.TryResolveModel(
            models.VideoModel,
            out ResolvedVideoModel resolved));
        Assert.Equal(expectedProfileId, resolved.ModelProfileId.Value);
        Assert.Same(Ltx2ArchitectureModule.Instance.Descriptor, resolved.Architecture);
    }
}
