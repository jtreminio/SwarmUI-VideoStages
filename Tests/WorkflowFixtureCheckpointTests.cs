using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Every checked-in fixture checkpoint is a bare safetensors header whose only job is to
/// classify. A silent misclassification would make every test built on it assert the wrong
/// architecture.
/// </summary>
[Collection("VideoStagesTests")]
public class WorkflowFixtureCheckpointTests
{
    [Theory]
    [InlineData(VideoStagesWorkflowFixture.BaseModelFixturePath, "stable-diffusion-xl-v1-base")]
    [InlineData(MiniMaxWorkflowFixture.ModelFixturePath, "minimax-h3")]
    [InlineData(Ltx2WorkflowFixture.ModelFixturePath, "lightricks-ltx-video-2-3")]
    [InlineData(Ltx2WorkflowFixture.NonV23ModelFixturePath, "lightricks-ltx-video-2")]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, "wan-2_2-image2video-14b")]
    [InlineData(WanWorkflowFixture.Wan22I2v14bHighNoiseFixturePath, "wan-2_2-image2video-14b")]
    [InlineData(WanWorkflowFixture.Wan22I2v14bLowNoiseFixturePath, "wan-2_2-image2video-14b")]
    [InlineData(WanWorkflowFixture.Wan22Ti2v5bFixturePath, "wan-2_2-ti2v-5b")]
    [InlineData(WanWorkflowFixture.Wan21I2v14bFixturePath, "wan-2_1-image2video-14b")]
    [InlineData(WanWorkflowFixture.Wan21T2v14bFixturePath, "wan-2_1-text2video-14b")]
    [InlineData("models/diffusion_models/Hunyuan15-Workflow-Test.safetensors", "hunyuan-video-1_5")]
    [InlineData("models/diffusion_models/Mochi-Workflow-Test.safetensors", "genmo-mochi-1")]
    public void Fixture_checkpoint_classifies_as_its_declared_architecture(
        string fixturePath,
        string expectedClassId)
    {
        using SafetensorsModelFixture models = SafetensorsModelFixture.Create(fixturePath);
        Assert.Equal(expectedClassId, models.Model.ModelClass.ID);
    }
}
