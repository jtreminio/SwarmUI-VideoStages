using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

public class Runner(WorkflowGenerator g)
{
    private readonly RootVideoStageTakeover _rootVideoStageTakeover = new(g);
    private readonly VideoStagesCoordinator _videoStagesCoordinator = new(g);

    public void CaptureCoreVideoControlNetPreprocessors()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        VideoStageControlNetApplicator.CaptureCoreVideoControlNetPreprocessors(g);
    }

    public void EnsureRootVideoStageModel()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        _rootVideoStageTakeover.EnsureRootVideoStageModel();
    }

    public void CaptureBase()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        _videoStagesCoordinator.CaptureBase();
    }

    public void CaptureRefiner()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        _videoStagesCoordinator.CaptureRefiner();
    }

    public void SuppressCoreRootVideoStage()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        _rootVideoStageTakeover.SuppressCoreRootVideoStage();
    }

    public void RestoreCoreRootVideoStageModel()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        _rootVideoStageTakeover.RestoreCoreRootVideoStageModel();
    }

    public void ApplyRootAudioMaskDimensionsAfterNativeVideo()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        LTX2.LtxAudioMaskResizer.ApplyRootAudioMaskDimensionsAfterNativeVideo(g);
    }

    public void RunConfiguredStages()
    {
        if (!IsExtensionActive())
        {
            return;
        }

        _videoStagesCoordinator.RunConfiguredStages();
    }

    private bool IsExtensionActive()
    {
        T2IParamType type = VideoStagesExtension.VideoStagesJson?.Type;
        return type is not null && g.UserInput.TryGetRaw(type, out _);
    }
}
