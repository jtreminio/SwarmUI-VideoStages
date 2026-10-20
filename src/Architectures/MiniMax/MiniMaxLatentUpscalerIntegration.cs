using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using VideoStages.Generated;

namespace VideoStages.Architectures.MiniMax;

internal static class MiniMaxLatentUpscalerIntegration
{
    internal const string InstallFeature = "minimax_h3_latent_upscaler";
    internal const string FeatureFlag = MinimaxH3LatentUpscaler3DNode.ClassType;
    internal const string NodeUrl = "https://github.com/LBH-123-AI/Comfyui_Minimax_h3_latent_Upscaler";

    internal static void RegisterDependencies()
    {
        InstallableFeatures.RegisterInstallableFeature(new(
            "MiniMax H3 Latent Upscaler",
            InstallFeature,
            NodeUrl,
            "LBH-123-AI",
            "This will install LBH's MiniMax H3 latent upscaler. SwarmUI will restart "
                + "its managed ComfyUI backends.\nDo you wish to install?"));
        ComfyUIBackendExtension.NodeToFeatureMap[MinimaxH3LatentUpscaler3DNode.ClassType] = FeatureFlag;
    }

    internal static string RenderGeneratedTypeScript() => $$"""
        // Generated from MiniMaxLatentUpscalerIntegration.cs. Do not edit by hand.

        export const H3_LATENT_UPSCALER_INSTALL_FEATURE = "{{InstallFeature}}";
        export const H3_LATENT_UPSCALER_FEATURE = "{{FeatureFlag}}";
        export const H3_LATENT_UPSCALER_REPOSITORY_URL =
            "{{NodeUrl}}";
        """ + "\n";
}
