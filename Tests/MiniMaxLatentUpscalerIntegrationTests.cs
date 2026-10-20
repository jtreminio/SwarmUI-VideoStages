using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using VideoStages.Architectures.MiniMax;
using VideoStages.Generated;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxLatentUpscalerIntegrationTests
{
    [Fact]
    public void Latent_upscaler_is_registered_with_the_host_installer()
    {
        Assert.True(InstallableFeatures.ComfyFeatures.TryGetValue(
            "minimax_h3_latent_upscaler",
            out InstallableFeatures.ComfyInstallableFeature feature));
        Assert.Equal("https://github.com/LBH-123-AI/Comfyui_Minimax_h3_latent_Upscaler", feature.URL);
        Assert.Equal("LBH-123-AI", feature.Author);
        Assert.False(feature.AutoInstall);
        Assert.Equal(
            MiniMaxLatentUpscalerIntegration.FeatureFlag,
            ComfyUIBackendExtension.NodeToFeatureMap[MinimaxH3LatentUpscaler3DNode.ClassType]);
    }

    [Fact]
    public void Frontend_installer_constants_match_the_backend()
    {
        Assert.Equal(
            MiniMaxLatentUpscalerIntegration.RenderGeneratedTypeScript(),
            RepoFiles.ReadFrontend("generatedMiniMaxLatentUpscaler.ts"));
    }
}
