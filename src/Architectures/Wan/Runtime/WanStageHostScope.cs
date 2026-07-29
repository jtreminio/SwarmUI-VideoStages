using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Pushes one Wan stage's compiled settings into a host parameter section so the host's own
/// image-to-video construction reads them. Disposing removes every section the scope opened,
/// including on an exceptional exit.
/// </summary>
internal sealed class WanStageHostScope(WorkflowGenerator g, VideoExecutionPlan plan) : IDisposable
{
    private readonly HashSet<int> _sectionIds = [];

    private readonly bool _publishIntermediateStages =
        plan.Clips.Sum(clip => clip.Stages.Count) > 1
        && g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
        && !g.UserInput.Get(T2IParamTypes.DoNotSave, false);

    private bool _disposed;

    internal int ApplyStageOverrides(ClipPlan clip, StagePlan stage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stage);
        WanStagePayload payload = stage.RequireWanPayload();

        int sectionId = VideoStagesExtension.SectionIdForStage(stage.StageId);
        _sectionIds.Add(sectionId);
        g.UserInput.SectionParamOverrides.Remove(sectionId);
        g.UserInput.Set(T2IParamTypes.VideoModel.Type, payload.Model, sectionId);
        g.UserInput.Set(T2IParamTypes.VideoSteps, payload.Steps, sectionId);
        g.UserInput.Set(T2IParamTypes.Steps, payload.Steps, sectionId);
        g.UserInput.Set(T2IParamTypes.VideoCFG, payload.CfgScale, sectionId);
        g.UserInput.Set(T2IParamTypes.CFGScale, payload.CfgScale, sectionId);
        g.UserInput.Set(ComfyUIBackendExtension.SamplerParam.Type, payload.Sampler, sectionId);
        g.UserInput.Set(ComfyUIBackendExtension.SchedulerParam.Type, payload.Scheduler, sectionId);
        if (clip.Frames is int frames && frames > 0)
        {
            g.UserInput.Set(T2IParamTypes.VideoFrames, frames, sectionId);
        }
        g.UserInput.Set(T2IParamTypes.VideoFPS, plan.FramesPerSecond, sectionId);
        return sectionId;
    }

    /// <summary>
    /// Saves a clip whose output is not the timeline's own, when the host asks for intermediates.
    /// Without this the setting would be silently ignored for Wan timelines.
    /// </summary>
    internal void PublishIntermediate(StagePlan stage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stage);
        if (!_publishIntermediateStages
            || stage.Output.IntermediatePolicy != IntermediateOutputPolicy.ControlledByHostSetting)
        {
            return;
        }
        g.CurrentMedia.SaveOutput(
            g.CurrentVae,
            g.CurrentAudioVae,
            StableNodeIds.Id(g, StableNodeIds.IntermediateStageSave, stage.StageId));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        foreach (int sectionId in _sectionIds)
        {
            g.UserInput.SectionParamOverrides.Remove(sectionId);
        }
        _disposed = true;
    }
}
