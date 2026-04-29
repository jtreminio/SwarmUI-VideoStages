using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class VideoStageModelCompatTests
{
    [Fact]
    public void IsWanVideoModel_RecognizesWan22_14bImage2VideoModel()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();
        Assert.True(VideoStageModelCompat.IsWanVideoModel(models.VideoModel));
        Assert.True(VideoStageModelCompat.SupportsWanFirstLastFrame(models.VideoModel));
    }

    [Fact]
    public void IsWanVideoModel_RejectsBlankName()
    {
        Assert.False(VideoStageModelCompat.IsWanVideoModel(""));
        Assert.False(VideoStageModelCompat.IsWanVideoModel("   "));
    }

    [Fact]
    public void SupportsWanFirstLastFrame_IsFalseForWan22_5bLatentPath()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22VideoModels();
        Assert.True(VideoStageModelCompat.IsWanVideoModel(models.VideoModel));
        Assert.False(VideoStageModelCompat.SupportsWanFirstLastFrame(models.VideoModel));
    }
}
