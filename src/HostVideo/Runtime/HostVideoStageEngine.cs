using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
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
    private readonly StageModelUpscaleGraphBuilder _modelScaler;
    private readonly HostVideoDecodedStageInput _decodedInput;
    private readonly StageHostExecutionScope _stageScope;

    internal HostVideoStageEngine(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        string architectureDisplayLabel)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(architectureDisplayLabel);
        _generator = generator;
        _trimmer = new(generator);
        _pixelScaler = new(generator);
        _modelScaler = new(generator);
        _decodedInput = new(
            generator,
            plan.FramesPerSecond,
            _trimmer,
            architectureDisplayLabel);
        _stageScope = new(generator, plan);
    }

    internal DecodedClipArtifact Execute(
        ClipPlan clip,
        Func<ClipPlan, StagePlan, int?> resolvePassthroughFrames,
        Action<ClipPlan, StagePlan, HostVideoDecodedStageInput, int> executeGeneratingStage)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(resolvePassthroughFrames);
        ArgumentNullException.ThrowIfNull(executeGeneratingStage);

        foreach (StagePlan stage in clip.Stages)
        {
            StageCorePlan settings = stage.Core;
            ApplyUpscale(stage, settings.Upscale);
            if (stage.IsPassthrough)
            {
                _decodedInput.ConfigurePassthrough(
                    clip,
                    stage,
                    resolvePassthroughFrames(clip, stage));
            }
            else
            {
                int sectionId = _stageScope.ApplyStageOverrides(clip, stage);
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
                bridge),
            clip);
    }

    public void Dispose() => _stageScope.Dispose();

    private void ApplyUpscale(StagePlan stage, StageUpscalePlan upscale)
    {
        if (upscale.Mode == StageUpscaleMode.None)
        {
            return;
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
        if (upscale.Mode == StageUpscaleMode.Model)
        {
            _modelScaler.Apply(
                _generator.CurrentMedia,
                targetWidth,
                targetHeight,
                upscale.MethodName);
            return;
        }
        _pixelScaler.Apply(
            _generator.CurrentMedia,
            targetWidth,
            targetHeight,
            upscale.MethodName);
    }
}
