using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

internal sealed class RootVideoStageTakeover(WorkflowGenerator g)
{
    private const int StashSectionId = Constants.SectionID_VideoStages;
    private const string SynthesizedRootVideoModelKey = "videostages.synth-root-video-model";

    public static bool IsTextToVideoRootWorkflow(WorkflowGenerator g)
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel existingVideoModel)
            && existingVideoModel is not null)
        {
            return false;
        }
        return g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true;
    }

    public bool ShouldReplaceTextToVideoRootStage(JsonParser.StageSpec stage)
    {
        return stage is not null
            && stage.ClipStageIndex == 0
            && IsTextToVideoRootWorkflow(g);
    }

    public void EnsureRootVideoStageModel()
    {
        if (HasNativeVideoModel())
        {
            return;
        }
        if (IsTextToVideoRootWorkflow(g))
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

    public void CleanupSynthesizedRootVideoStageModel()
    {
        if (g.NodeHelpers.Remove(SynthesizedRootVideoModelKey) == true)
        {
            g.UserInput.Remove(T2IParamTypes.VideoModel);
        }
    }

    public bool ShouldTakeOverRootStage()
    {
        if (VideoStagesExtension.CoreImageToVideoStep is null)
        {
            return false;
        }
        bool hasNativeVideoModel = HasNativeVideoModel();
        bool hasTextToVideoRootModel = IsTextToVideoRootWorkflow(g);
        if (!hasNativeVideoModel && !hasTextToVideoRootModel)
        {
            return false;
        }
        if (hasNativeVideoModel
            && !WorkflowGenerator.Steps.Contains(VideoStagesExtension.CoreImageToVideoStep))
        {
            return false;
        }
        return new JsonParser(g).ParseStages().Count > 0;
    }

    public void SuppressCoreRootVideoStage()
    {
        if (!ShouldTakeOverRootStage())
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

    public void RestoreCoreRootVideoStageModel()
    {
        if (HasNativeVideoModel())
        {
            CleanupStashSection();
            return;
        }
        if (!g.UserInput.TryGet(
                T2IParamTypes.VideoModel,
                out T2IModel stashedModel,
                sectionId: StashSectionId,
                includeBase: false))
        {
            CleanupStashSection();
            return;
        }

        g.UserInput.Set(T2IParamTypes.VideoModel, stashedModel);
        g.UserInput.Remove(T2IParamTypes.VideoModel, StashSectionId);
        CleanupStashSection();
    }

    private void CleanupStashSection()
    {
        if (g.UserInput.SectionParamOverrides.TryGetValue(
                StashSectionId,
                out T2IParamSet stash)
            && stash.ValuesInput.Count == 0)
        {
            g.UserInput.SectionParamOverrides.Remove(StashSectionId);
        }
    }

    private bool HasNativeVideoModel()
    {
        return g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel _);
    }
}
