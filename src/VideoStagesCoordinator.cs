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
        if (!HasConfiguredStages())
        {
            return;
        }

        new StageRefStore(g).Capture(StageRefStore.StageKind.Base);
    }

    public void CaptureRefiner()
    {
        if (!HasConfiguredStages())
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

        List<JsonParser.StageSpec> stages = HasConfiguredStages()
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
        return g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel rootVideoModel) && rootVideoModel is not null;
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

        T2IParamType type = VideoStagesExtension.VideoStagesJson?.Type;
        if (type is null
            || !g.UserInput.TryGetRaw(type, out object rawValue)
            || rawValue is not string json
            || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        return json.Trim() != "[]";
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
