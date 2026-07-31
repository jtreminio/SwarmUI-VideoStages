using SwarmUI.Text2Image;
namespace VideoStages.Architectures.Ltx2;

internal static class Ltx2ModelCompatibility
{
    internal static bool IsLtxV2VideoModel(T2IModel model)
    {
        return Ltx2ArchitectureModule.Instance.TryResolveModel(model, out _);
    }
}
