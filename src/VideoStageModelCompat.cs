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

    internal static bool IsWanVideoModel(T2IModel model)
    {
        if (model?.ModelClass is null)
        {
            return false;
        }

        string compatId = model.ModelClass.CompatClass?.ID ?? "";
        return compatId == T2IModelClassSorter.CompatWan21.ID
            || compatId == T2IModelClassSorter.CompatWan21_14b.ID
            || compatId == T2IModelClassSorter.CompatWan21_1_3b.ID
            || compatId == T2IModelClassSorter.CompatWan22_5b.ID;
    }

    internal static bool IsWanVideoModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        if (Program.T2IModelSets.TryGetValue("Stable-Diffusion", out T2IModelHandler sdHandler))
        {
            return IsWanVideoModel(sdHandler.GetModel(modelName, null));
        }

        return false;
    }

    internal static bool SupportsWanFirstLastFrame(T2IModel model)
    {
        if (!IsWanVideoModel(model))
        {
            return false;
        }

        if (model.ModelClass.CompatClass?.ID == T2IModelClassSorter.CompatWan22_5b.ID)
        {
            return false;
        }

        return true;
    }
}
