using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class IcLoraWeightsTests
{
    [Fact]
    public void Generated_typescript_auto_model_tokens_are_current()
    {
        Assert.Equal(
            IcLoraWeights.RenderGeneratedTypeScript(),
            RepoFiles.ReadFrontend("architectures/ltx2/generatedIcLora.ts"));
    }
}
