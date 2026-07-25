using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using VideoStages.Generated;

namespace VideoStages.Architectures.Ltx2;

/// <summary>LTX host feature registration and custom-node integration.</summary>
internal static class Ltx2HostIntegration
{
    internal const string FeatureFlag = "ltxvideo";
    internal const string NodeUrl = "https://github.com/Lightricks/ComfyUI-LTXVideo";

    internal static void RegisterDependencies()
    {
        InstallableFeatures.RegisterInstallableFeature(new(
            "LTXVideo",
            FeatureFlag,
            NodeUrl,
            "Lightricks",
            "This will install LTXVideo ComfyUI nodes developed by Lightricks.\n"
            + "If you already installed ComfyUI-LTXVideo in your ComfyUI custom_nodes folder, "
            + "you do not need to install it again.\n"
            + "Do you wish to install?"));

        ComfyUIBackendExtension.NodeToFeatureMap[LTXICLoRALoaderModelOnlyNode.ClassType] =
            FeatureFlag;
        ComfyUIBackendExtension.NodeToFeatureMap[LTXAddVideoICLoRAGuideNode.ClassType] =
            FeatureFlag;
        ComfyUIBackendExtension.NodeToFeatureMap[LTXAddVideoICLoRAGuideAdvancedNode.ClassType] =
            FeatureFlag;

        if (!ComfyUISelfStartBackend.FoldersToForwardInComfyPath.Contains(
                "geometry_estimation"))
        {
            ComfyUISelfStartBackend.FoldersToForwardInComfyPath.Add("geometry_estimation");
        }
    }

    internal static bool IsAvailable(IReadOnlyCollection<string> features) =>
        features.Contains(FeatureFlag);
}
