using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using VideoStages.Architectures.MiniMax;
using VideoStages.Authoring;
using VideoStages.Generated;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxTextEncoderTests
{
    [Fact]
    public void ClipProj_feature_folder_and_generated_text_encoder_options_are_current()
    {
        Assert.Equal(
            MiniMaxTextEncoderGraph.FeatureFlag,
            ComfyUIBackendExtension.NodeToFeatureMap[ClipProjApplyNode.ClassType]);
        Assert.Contains(
            "clip_projections",
            ComfyUISelfStartBackend.FoldersToForwardInComfyPath);
        Assert.True(InstallableFeatures.ComfyFeatures.TryGetValue(
            MiniMaxTextEncoderGraph.FeatureFlag,
            out InstallableFeatures.ComfyInstallableFeature feature));
        Assert.Equal(MiniMaxTextEncoderGraph.NodeUrl, feature.URL);
        Assert.Equal(
            MiniMaxTextEncoders.RenderGeneratedTypeScript(),
            RepoFiles.ReadFrontend("generatedMiniMaxTextEncoder.ts"));
    }

    [Theory]
    [InlineData(
        MiniMaxTextEncoder.Qwen3Vl8B,
        "mmh3-8b-ClipProj-v3-mlp.safetensors",
        "9304d6002db92eb1ac58dac917864b3f8b96bf0d65fd889e6f20de18413a091c")]
    [InlineData(
        MiniMaxTextEncoder.Qwen3Vl4B,
        "mmh3-4b-ClipProj-v3-mlp.safetensors",
        "feef06ef3b9aede3b1f3331b71eebbc873e21a867d73bcf40ea2c0b007270693")]
    public void Selected_text_encoder_owns_a_verified_Hugging_Face_projection(
        MiniMaxTextEncoder selection,
        string expectedFileName,
        string expectedSha256)
    {
        (string fileName, string url, string sha256) =
            MiniMaxTextEncoderGraph.ProjectionFor(selection);

        Assert.Equal(expectedFileName, fileName);
        Assert.Equal(
            $"https://huggingface.co/NicoLab28/ClipProj-MiniMax-H3/resolve/main/{fileName}",
            url);
        Assert.Equal(expectedSha256, sha256);
    }
}
