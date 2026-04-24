using System.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal static class RootVideoStageTakeover
{
    private const int StashSectionId = VideoStagesExtension.SectionID_VideoStages;
    private const string SynthesizedRootVideoModelKey = "videostages.synth-root-video-model";

    public static void EnsureRootVideoStageModel(WorkflowGenerator g)
    {
        if (g is null || g.UserInput is null)
        {
            return;
        }
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel _))
        {
            return;
        }
        if (g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true)
        {
            return;
        }

        JsonParser.StageSpec firstStage = new JsonParser(g).ParseStages().FirstOrDefault();
        if (firstStage is null || string.IsNullOrWhiteSpace(firstStage.Model))
        {
            return;
        }

        g.UserInput.Set(T2IParamTypes.VideoModel.Type, firstStage.Model);
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel _))
        {
            g.NodeHelpers[SynthesizedRootVideoModelKey] = "1";
            return;
        }

        g.UserInput.Remove(T2IParamTypes.VideoModel);
        Logs.Warning($"VideoStages: could not resolve root video model '{firstStage.Model}'.");
    }

    public static void CleanupSynthesizedRootVideoStageModel(WorkflowGenerator g)
    {
        if (g?.NodeHelpers?.Remove(SynthesizedRootVideoModelKey) == true)
        {
            g.UserInput.Remove(T2IParamTypes.VideoModel);
        }
    }

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
        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel _))
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
        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel))
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
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel _))
        {
            CleanupStashSection(g);
            return;
        }
        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel stashedModel, sectionId: StashSectionId, includeBase: false))
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
