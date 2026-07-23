using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Owns temporary host section overrides and host-facing stage publication for one sequence run.
/// Disposing the scope removes every section it touched, including exceptional exits.
/// </summary>
internal sealed class StageHostExecutionScope : IDisposable
{
    private const int IntermediateStageSaveId = 52100;

    private readonly WorkflowGenerator _generator;
    private readonly VideoExecutionPlan _plan;
    private readonly HashSet<int> _sectionIds = [];
    private readonly bool _publishIntermediateStages;
    private bool _disposed;

    public StageHostExecutionScope(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        bool parallelMultiClip)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        int totalStageCount = plan.Clips.Sum(clip => clip.Stages.Count);
        _publishIntermediateStages =
            totalStageCount > 1
            && generator.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            && !generator.UserInput.Get(T2IParamTypes.DoNotSave, false);
        ExecutionOptions = new StageExecutionOptions(
            parallelMultiClip,
            _publishIntermediateStages);
    }

    public StageExecutionOptions ExecutionOptions { get; }

    public int ApplyStageOverrides(
        ClipContext clipContext,
        ClipPlan plannedClip,
        StagePlan plannedStage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(clipContext);
        ArgumentNullException.ThrowIfNull(plannedClip);
        ArgumentNullException.ThrowIfNull(plannedStage);
        Ltx2StagePayload payload = plannedStage.RequireLtx2Payload();

        int sectionId = VideoStagesExtension.SectionIdForStage(plannedStage.StageId);
        _sectionIds.Add(sectionId);
        _generator.UserInput.SectionParamOverrides.Remove(sectionId);
        _generator.UserInput.Set(T2IParamTypes.VideoModel.Type, payload.Core.Model, sectionId);
        _generator.UserInput.Set(T2IParamTypes.VideoSteps, payload.Core.Steps, sectionId);
        _generator.UserInput.Set(T2IParamTypes.Steps, payload.Core.Steps, sectionId);
        _generator.UserInput.Set(T2IParamTypes.VideoCFG, payload.Core.CfgScale, sectionId);
        _generator.UserInput.Set(T2IParamTypes.CFGScale, payload.Core.CfgScale, sectionId);
        _generator.UserInput.Set(
            ComfyUIBackendExtension.SamplerParam.Type,
            payload.Core.Sampler,
            sectionId);
        _generator.UserInput.Set(
            ComfyUIBackendExtension.SchedulerParam.Type,
            payload.Core.Scheduler,
            sectionId);
        if (plannedClip.Frames is int frames && frames > 0)
        {
            _generator.UserInput.Set(T2IParamTypes.VideoFrames, frames, sectionId);
        }
        if (_plan.FramesPerSecond > 0)
        {
            _generator.UserInput.Set(T2IParamTypes.VideoFPS, _plan.FramesPerSecond, sectionId);
        }
        if (clipContext.Dimensions.Width > 0)
        {
            _generator.UserInput.Set(T2IParamTypes.Width, clipContext.Dimensions.Width, sectionId);
        }
        if (clipContext.Dimensions.Height > 0)
        {
            _generator.UserInput.Set(T2IParamTypes.Height, clipContext.Dimensions.Height, sectionId);
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
            _generator.GetStableDynamicID(IntermediateStageSaveId, stage.StageId));
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
