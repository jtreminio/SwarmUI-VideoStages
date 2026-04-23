using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

internal static class RootVideoStageTakeover
{
    private const int StashSectionId = VideoStagesExtension.SectionID_VideoStages;

    public static bool ShouldTakeOverRootStage(WorkflowGenerator g)
    {
        if (g is null || g.UserInput is null || VideoStagesExtension.CoreImageToVideoStep is null)
        {
            return false;
        }
        if (!WorkflowGenerator.Steps.Contains(VideoStagesExtension.CoreImageToVideoStep))
        {
            return false;
        }
        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel) || videoModel is null)
        {
            return false;
        }
        return new JsonParser(g).ParseStages().Count > 0;
    }

    public static void SuppressCoreRootVideoStage(WorkflowGenerator g)
    {
        if (!ShouldTakeOverRootStage(g))
        {
            return;
        }
        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel) || videoModel is null)
        {
            return;
        }

        g.UserInput.Set(T2IParamTypes.VideoModel, videoModel, StashSectionId);
        g.UserInput.Remove(T2IParamTypes.VideoModel);
    }

    public static void RestoreCoreRootVideoStageModel(WorkflowGenerator g)
    {
        if (g is null || g.UserInput is null)
        {
            return;
        }

        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel activeModel) && activeModel is not null)
        {
            CleanupStashSection(g);
            return;
        }
        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel stashedModel, sectionId: StashSectionId, includeBase: false)
            || stashedModel is null)
        {
            CleanupStashSection(g);
            return;
        }

        g.UserInput.Set(T2IParamTypes.VideoModel, stashedModel);
        g.UserInput.Remove(T2IParamTypes.VideoModel, StashSectionId);
        CleanupStashSection(g);
    }

    private static void CleanupStashSection(WorkflowGenerator g)
    {
        if (g.UserInput.SectionParamOverrides.TryGetValue(StashSectionId, out T2IParamSet stash)
            && stash.ValuesInput.Count == 0)
        {
            g.UserInput.SectionParamOverrides.Remove(StashSectionId);
        }
    }
}
