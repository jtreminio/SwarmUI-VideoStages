using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.HostVideo.Runtime;

/// <summary>The host media a stock-video timeline snapshots before clip execution.</summary>
internal sealed record HostVideoRootSources(WGNodeData Media, WGNodeData Vae);

/// <summary>
/// Runs the stage-loop mechanics proven equivalent for WAN and generic-host video. Architecture
/// callbacks retain model loading, conditioning, native text entry, references, and cleanup.
/// </summary>
internal sealed class HostVideoStageEngine : IDisposable
{
    private readonly WorkflowGenerator _generator;
    private readonly GlobalVideoFrameTrimmer _trimmer;
    private readonly StagePixelScaleGraphBuilder _pixelScaler;
    private readonly HostVideoDecodedStageInput _decodedInput;
    private readonly HostVideoStageScope _stageScope;
    private readonly string _architectureDisplayLabel;

    internal HostVideoStageEngine(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        string architectureDisplayLabel)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(architectureDisplayLabel);
        _generator = generator;
        _architectureDisplayLabel = architectureDisplayLabel;
        _trimmer = new(generator);
        _pixelScaler = new(generator);
        _decodedInput = new(
            generator,
            plan.FramesPerSecond,
            _trimmer,
            architectureDisplayLabel);
        _stageScope = new(generator, plan);
    }

    internal DecodedClipArtifact Execute(
        ClipPlan clip,
        Func<StagePlan, IHostVideoStageSettings> resolveSettings,
        Func<ClipPlan, StagePlan, int?> resolvePassthroughFrames,
        Action<ClipPlan, StagePlan, HostVideoDecodedStageInput, int> executeGeneratingStage)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(resolveSettings);
        ArgumentNullException.ThrowIfNull(resolvePassthroughFrames);
        ArgumentNullException.ThrowIfNull(executeGeneratingStage);

        foreach (StagePlan stage in clip.Stages)
        {
            IHostVideoStageSettings settings = resolveSettings(stage)
                ?? throw new InvalidOperationException(
                    $"Stage {stage.StageId} has no {_architectureDisplayLabel} host settings.");
            ApplyPixelUpscale(stage, settings.Upscale);
            if (stage.IsPassthrough)
            {
                _decodedInput.ConfigurePassthrough(
                    clip,
                    stage,
                    resolvePassthroughFrames(clip, stage));
            }
            else
            {
                int sectionId = _stageScope.ApplyStageOverrides(clip, stage, settings);
                executeGeneratingStage(clip, stage, _decodedInput, sectionId);
            }
            _stageScope.PublishIntermediate(stage);
        }

        StagePlan finalStage = clip.Stages[^1];
        if (finalStage.Output.IsTimelineTerminal && _trimmer.IsRequested)
        {
            _trimmer.Apply();
        }
        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        return DecodedClipArtifact.FromRuntime(
            RuntimeArtifact.Capture(
                _generator,
                bridge,
                ArtifactOrigin.StageOutput),
            clip);
    }

    public void Dispose() => _stageScope.Dispose();

    private void ApplyPixelUpscale(StagePlan stage, StageUpscalePlan upscale)
    {
        if (upscale.Mode == StageUpscaleMode.None)
        {
            return;
        }
        if (upscale.Mode != StageUpscaleMode.Pixel)
        {
            throw new InvalidOperationException(
                $"Stage {stage.StageId} reached the {_architectureDisplayLabel} runtime with "
                    + $"unsupported upscale method '{upscale.RawMethod}'.");
        }
        if (_generator.CurrentMedia is null)
        {
            // Native text entry has no decoded pixels to resize.
            return;
        }
        int width = _generator.CurrentMedia.Width
            ?? throw new InvalidOperationException(
                $"Stage {stage.StageId} cannot pixel-scale media with no width.");
        int height = _generator.CurrentMedia.Height
            ?? throw new InvalidOperationException(
                $"Stage {stage.StageId} cannot pixel-scale media with no height.");
        (int targetWidth, int targetHeight) = DimensionSnap.Snap(
            width * upscale.Factor,
            height * upscale.Factor);
        _pixelScaler.Apply(
            _generator.CurrentMedia,
            targetWidth,
            targetHeight,
            upscale.MethodName);
    }
}

/// <summary>
/// Pushes a compiled stock-host stage into its host parameter section. Disposal removes every
/// section opened by the timeline, including after an exceptional exit.
/// </summary>
internal sealed class HostVideoStageScope(
    WorkflowGenerator generator,
    VideoExecutionPlan plan) : IDisposable
{
    private readonly HashSet<int> _sectionIds = [];
    private readonly bool _publishIntermediateStages =
        plan.Clips.Sum(clip => clip.Stages.Count) > 1
        && generator.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
        && !generator.UserInput.Get(T2IParamTypes.DoNotSave, false);
    private bool _disposed;

    internal int ApplyStageOverrides(
        ClipPlan clip,
        StagePlan stage,
        IHostVideoStageSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(settings);

        int sectionId = VideoStagesExtension.SectionIdForStage(stage.StageId);
        _sectionIds.Add(sectionId);
        generator.UserInput.SectionParamOverrides.Remove(sectionId);
        generator.UserInput.Set(T2IParamTypes.VideoModel.Type, settings.Model, sectionId);
        generator.UserInput.Set(T2IParamTypes.VideoSteps, settings.Steps, sectionId);
        generator.UserInput.Set(T2IParamTypes.Steps, settings.Steps, sectionId);
        generator.UserInput.Set(T2IParamTypes.VideoCFG, settings.CfgScale, sectionId);
        generator.UserInput.Set(T2IParamTypes.CFGScale, settings.CfgScale, sectionId);
        generator.UserInput.Set(
            ComfyUIBackendExtension.SamplerParam.Type,
            settings.Sampler,
            sectionId);
        generator.UserInput.Set(
            ComfyUIBackendExtension.SchedulerParam.Type,
            settings.Scheduler,
            sectionId);
        if (clip.Frames is int frames && frames > 0)
        {
            generator.UserInput.Set(T2IParamTypes.VideoFrames, frames, sectionId);
        }
        generator.UserInput.Set(T2IParamTypes.VideoFPS, plan.FramesPerSecond, sectionId);
        return sectionId;
    }

    internal void PublishIntermediate(StagePlan stage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stage);
        if (!_publishIntermediateStages
            || stage.Output.IntermediatePolicy
                != IntermediateOutputPolicy.ControlledByHostSetting)
        {
            return;
        }
        generator.CurrentMedia.SaveOutput(
            generator.CurrentVae,
            generator.CurrentAudioVae,
            StableNodeIds.Id(
                generator,
                StableNodeIds.IntermediateStageSave,
                stage.StageId));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        foreach (int sectionId in _sectionIds)
        {
            generator.UserInput.SectionParamOverrides.Remove(sectionId);
        }
        _disposed = true;
    }
}
