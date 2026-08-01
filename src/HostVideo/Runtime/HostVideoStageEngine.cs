using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.HostVideo.Runtime;

/// <summary>Host media captured before clip execution.</summary>
internal sealed record HostVideoRootSources(WGNodeData Media, WGNodeData Vae);

/// <summary>
/// Runs the shared stage loop for WAN and generic host video. Architecture callbacks handle
/// model loading, conditioning, native text entry, references, and cleanup.
/// </summary>
internal sealed class HostVideoStageEngine : IDisposable
{
    private readonly WorkflowGenerator _generator;
    private readonly GlobalVideoFrameTrimmer _trimmer;
    private readonly StageUpscaleGraph _upscaleGraph;
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
        _upscaleGraph = new(generator);
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
        if (upscale.Mode is not (StageUpscaleMode.Pixel or StageUpscaleMode.Model))
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                _generator.UserInput,
                $"VideoStages: Stage {stage.StageId} uses unsupported upscale method "
                    + $"'{upscale.RawMethod}'. Ignoring upscale.");
            return;
        }
        int width = _generator.CurrentMedia.Width
            ?? throw VideoStagesInvariant.Failure(
                $"Stage {stage.StageId} cannot pixel-scale media with no width.");
        int height = _generator.CurrentMedia.Height
            ?? throw VideoStagesInvariant.Failure(
                $"Stage {stage.StageId} cannot pixel-scale media with no height.");
        (int targetWidth, int targetHeight) = StageUpscaleGraph.ResolveTargetDimensions(
            width,
            height,
            upscale.Factor);
        _upscaleGraph.Apply(
            _generator.CurrentMedia,
            targetWidth,
            targetHeight,
            upscale.Mode,
            upscale.MethodName);
    }
}
