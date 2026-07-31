using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;

namespace VideoStages.Execution;

/// <summary>
/// Owns temporary host section overrides and host-facing stage publication for one sequence run.
/// Disposing the scope removes every section it touched, including exceptional exits.
/// </summary>
internal sealed class StageHostExecutionScope : IDisposable
{
    private readonly WorkflowGenerator _generator;
    private readonly VideoExecutionPlan _plan;
    private readonly HashSet<int> _sectionIds = [];
    private readonly bool _publishIntermediateStages;
    private bool _disposed;

    public StageHostExecutionScope(
        WorkflowGenerator generator,
        VideoExecutionPlan plan)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        int totalStageCount = plan.Clips.Sum(clip => clip.Stages.Count);
        _publishIntermediateStages =
            totalStageCount > 1
            && generator.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            && !generator.UserInput.Get(T2IParamTypes.DoNotSave, false);
    }

    public bool PublishesIntermediateStages => _publishIntermediateStages;

    public int ApplyStageOverrides(
        ClipPlan plannedClip,
        StagePlan plannedStage,
        int? width = null,
        int? height = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plannedClip);
        ArgumentNullException.ThrowIfNull(plannedStage);
        StageCorePlan core = plannedStage.Core;

        int sectionId = VideoStagesExtension.SectionIdForStage(plannedStage.StageId);
        _sectionIds.Add(sectionId);
        _generator.UserInput.SectionParamOverrides.Remove(sectionId);
        _generator.UserInput.Set(
            T2IParamTypes.VideoModel.Type,
            plannedStage.ResolvedModel.ModelName,
            sectionId);
        _generator.UserInput.Set(T2IParamTypes.VideoSteps, core.Steps, sectionId);
        _generator.UserInput.Set(T2IParamTypes.Steps, core.Steps, sectionId);
        _generator.UserInput.Set(T2IParamTypes.VideoCFG, core.CfgScale, sectionId);
        _generator.UserInput.Set(T2IParamTypes.CFGScale, core.CfgScale, sectionId);
        _generator.UserInput.Set(
            ComfyUIBackendExtension.SamplerParam.Type,
            core.Sampler,
            sectionId);
        _generator.UserInput.Set(
            ComfyUIBackendExtension.SchedulerParam.Type,
            core.Scheduler,
            sectionId);
        if (plannedClip.Frames is int frames && frames > 0)
        {
            _generator.UserInput.Set(T2IParamTypes.VideoFrames, frames, sectionId);
        }
        if (_plan.FramesPerSecond > 0)
        {
            _generator.UserInput.Set(T2IParamTypes.VideoFPS, _plan.FramesPerSecond, sectionId);
        }
        if (width > 0)
        {
            _generator.UserInput.Set(T2IParamTypes.Width, width.Value, sectionId);
        }
        if (height > 0)
        {
            _generator.UserInput.Set(T2IParamTypes.Height, height.Value, sectionId);
        }
        return sectionId;
    }

    public void PublishStageInput(RuntimeArtifact inputArtifact)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(inputArtifact);
        inputArtifact.PublishTo(_generator);
    }

    public void PublishIntermediate(StagePlan stage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stage);
        if (!_publishIntermediateStages
            || stage.Output.IntermediatePolicy != IntermediateOutputPolicy.ControlledByHostSetting)
        {
            return;
        }
        _generator.CurrentMedia.SaveOutput(
            _generator.CurrentVae,
            _generator.CurrentAudioVae,
            StableNodeIds.Id(_generator, StableNodeIds.IntermediateStageSave, stage.StageId));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        foreach (int sectionId in _sectionIds)
        {
            _generator.UserInput.SectionParamOverrides.Remove(sectionId);
        }
        _disposed = true;
    }
}
