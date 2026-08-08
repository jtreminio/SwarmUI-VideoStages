using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class StageUpscalePlanTests
{
    [Fact]
    public void Generated_typescript_upscale_modes_are_current()
    {
        Assert.Equal(
            StageUpscalePlanCompiler.RenderGeneratedTypeScript(),
            RepoFiles.ReadFrontend("generatedUpscaleModes.ts"));
    }
}
