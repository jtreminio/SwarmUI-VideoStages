using System.Collections.Generic;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.LTX2;

namespace VideoStages;

public class VideoStagesCoordinator(WorkflowGenerator g)
{
    private const int FinalStageSaveId = 52200;

    public void CaptureBase()
    {
        if (!ShouldCaptureStageRefs())
        {
            return;
        }

        new StageRefStore(g).Capture(StageRefStore.StageKind.Base);
    }

    public void CaptureRefiner()
    {
        if (!ShouldCaptureStageRefs())
        {
            return;
        }

        new StageRefStore(g).Capture(StageRefStore.StageKind.Refiner);
    }

    public void RunConfiguredStages()
    {
        if (!HasRootVideoModel())
        {
            return;
        }

        TryInjectDetectedAudio();

        List<JsonParser.StageSpec> stages = IsVideoStagesEnabledForVideo()
            ? new JsonParser(g).ParseStages()
            : [];
        if (stages.Count == 0)
        {
            return;
        }

        new StageSequenceRunner(g, new StageRefStore(g), stages).Run();
        EnsureFinalStageOutputSaved();
    }

    private void TryInjectDetectedAudio()
    {
        if (!g.UserInput.Get(VideoStagesExtension.ConnectAudioToVideo, true))
        {
            return;
        }

        if (!g.IsLTXV2() || g.CurrentAudioVae is null)
        {
            return;
        }

        AudioStageDetector.Detection detection = new AudioStageDetector(g).Detect();
        if (detection is null)
        {
            return;
        }

        _ = new AudioInjector(g).TryInject(detection);
    }

    private bool HasRootVideoModel()
    {
        return GetRootVideoModel() is not null;
    }

    private T2IModel GetRootVideoModel()
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel imageToVideoModel) && imageToVideoModel is not null)
        {
            return imageToVideoModel;
        }

        if (g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true)
        {
            return textToVideoModel;
        }

        return null;
    }

    private bool IsVideoStagesEnabledForVideo()
    {
        if (!g.UserInput.Get(VideoStagesExtension.EnableVideoStages, false))
        {
            return false;
        }

        return HasRootVideoModel();
    }

    private bool HasConfiguredStages()
    {
        if (!IsVideoStagesEnabledForVideo())
        {
            return false;
        }

        return new JsonParser(g).HasConfiguredStages();
    }

    private bool ShouldCaptureStageRefs()
    {
        if (!HasRootVideoModel())
        {
            return false;
        }

        return HasConfiguredStages()
            || HasConfiguredRootGuideReference(VideoStagesExtension.RootGuideImageReference)
            || HasConfiguredRootGuideReference(VideoStagesExtension.RootGuideLastFrameReference);
    }

    private bool HasConfiguredRootGuideReference(T2IRegisteredParam<string> param)
    {
        string compact = ImageReferenceSyntax.Compact(g.UserInput.Get(param, "Default"));
        return !string.IsNullOrWhiteSpace(compact)
            && !string.Equals(compact, "Default", System.StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureFinalStageOutputSaved()
    {
        if (g.UserInput.Get(T2IParamTypes.DoNotSave, false) || g.CurrentMedia is null)
        {
            return;
        }

        if (g.CurrentMedia.Path is not { Count: 2 })
        {
            g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, g.GetStableDynamicID(FinalStageSaveId, 0));
            return;
        }

        if (WorkflowUtils.IsNodeTypeReachableFromOutput(g.Workflow, g.CurrentMedia.Path, NodeTypes.SwarmSaveAnimationWS))
        {
            return;
        }

        g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, g.GetStableDynamicID(FinalStageSaveId, 0));
    }
}
