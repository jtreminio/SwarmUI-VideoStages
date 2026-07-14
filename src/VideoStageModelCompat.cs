using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace VideoStages;

internal static class VideoStageModelCompat
{
    internal static bool IsLtxV2VideoModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }
        T2IModel model = Program.MainSDModels.GetModel(modelName, null);
        return model?.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatLtxv2.ID;
    }

    internal static bool IsLtxV2VideoModel(T2IModel model)
    {
        return model?.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatLtxv2.ID;
    }

    /// <summary>
    /// Overload for join points with only a resolved compat class, no model handle (e.g. the
    /// multi-clip merger's pixel output <see cref="SwarmUI.Builtin_ComfyUIBackend.WGNodeData.Compat"/>).
    /// </summary>
    internal static bool IsLtxV2VideoModel(T2IModelCompatClass compat)
    {
        return compat is not null && compat.ID == T2IModelClassSorter.CompatLtxv2.ID;
    }
}
