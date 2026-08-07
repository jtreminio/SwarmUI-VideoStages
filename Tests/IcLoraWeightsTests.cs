using System.Runtime.CompilerServices;
using VideoStages.Architectures.Ltx2;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class IcLoraWeightsTests
{
    [Fact]
    public void Generated_typescript_auto_model_tokens_are_current()
    {
        string committedPath = Path.GetFullPath(
            Path.Combine(TestSourceDirectory(), "..", "frontend", "architectures", "ltx2",
                "generatedIcLora.ts"));

        Assert.Equal(
            IcLoraWeights.RenderGeneratedTypeScript(),
            File.ReadAllText(committedPath));
    }

    private static string TestSourceDirectory(
        [CallerFilePath] string sourcePath = "") =>
        Path.GetDirectoryName(sourcePath)!;
}
