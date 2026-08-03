using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.HostVideo.Runtime;

/// <summary>Host media captured before clip execution.</summary>
internal sealed record HostVideoRootSources(WGNodeData Media, WGNodeData Vae);

/// <summary>
/// Runs the shared authored-stage loop.
/// </summary>
internal sealed class VideoStageRunner : IDisposable
{
    private readonly WorkflowGenerator _generator;
    private readonly GlobalVideoFrameTrimmer _trimmer;
    private readonly StageUpscaleGraph _upscaleGraph;
    private readonly HostVideoDecodedStageInput _decodedInput;
    private readonly StageHostExecutionScope _stageScope;

    internal VideoStageRunner(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        string architectureDisplayLabel,
        bool preserveAttachedAudio = false)
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
            architectureDisplayLabel,
            preserveAttachedAudio);
        _stageScope = new(generator, plan);
    }

    internal DecodedClipArtifact Execute(
        ClipPlan clip,
        Func<ClipPlan, StagePlan, int?> resolvePassthroughFrames,
        Func<
            ClipPlan,
            StagePlan,
            StagePlan,
            HostVideoDecodedStageInput,
            int,
            bool> executeGeneratingStage)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(resolvePassthroughFrames);
        ArgumentNullException.ThrowIfNull(executeGeneratingStage);

        _ = ExecuteStages(clip, (stage, continuation) =>
        {
            StageCorePlan settings = stage.Core;
            ApplyUpscale(stage, settings.Upscale);
            bool consumedContinuation = false;
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
                if (continuation is not null)
                {
                    _stageScope.ApplyStageOverrides(clip, continuation);
                    _stageScope.ApplySamplingContinuationOverrides(continuation);
                }
                consumedContinuation = executeGeneratingStage(
                    clip,
                    stage,
                    continuation,
                    _decodedInput,
                    sectionId);
            }
            return consumedContinuation;
        });

        StagePlan finalStage = clip.Stages[^1];
        if (finalStage.Output.IsTimelineTerminal && _trimmer.IsRequested)
        {
            _trimmer.Apply();
        }
        return DecodedClipArtifact.FromRuntime(
            CaptureRuntimeArtifact(),
            clip);
    }

    internal RuntimeArtifact ExecuteStages(
        ClipPlan clip,
        Func<StagePlan, StagePlan, bool> executeStage)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(executeStage);

        RuntimeArtifact output = null;
        for (int stageIndex = 0; stageIndex < clip.Stages.Count; stageIndex++)
        {
            StagePlan stage = clip.Stages[stageIndex];
            StagePlan continuation = !stage.IsPassthrough
                && stageIndex + 1 < clip.Stages.Count
                && clip.Stages[stageIndex + 1].ContinuesSamplingFromPreviousStage
                    ? clip.Stages[stageIndex + 1]
                    : null;
            RuntimeArtifact input = output ?? CaptureRuntimeArtifact();
            _stageScope.PublishStageInput(input);
            bool consumedContinuation = executeStage(stage, continuation);
            output = CaptureRuntimeArtifact();
            if (!output.HasMedia)
            {
                throw VideoStagesInvariant.Failure(
                    $"VideoStages: stage {stage.StageId} produced no media artifact.");
            }
            if (consumedContinuation && continuation is null)
            {
                throw VideoStagesInvariant.Failure(
                    $"VideoStages: stage {stage.StageId} consumed no planned continuation.");
            }
            _stageScope.PublishIntermediate(
                consumedContinuation ? continuation : stage);
            if (consumedContinuation)
            {
                stageIndex++;
            }
        }
        return output;
    }

    internal bool PublishesIntermediateStages =>
        _stageScope.PublishesIntermediateStages;

    internal int ApplyStageOverrides(
        ClipPlan clip,
        StagePlan stage,
        int? width,
        int? height,
        int? frameCount) =>
        _stageScope.ApplyStageOverrides(
            clip,
            stage,
            width,
            height,
            frameCount);

    internal void PublishIntermediate(
        StagePlan stage,
        WGNodeData media,
        WGNodeData vae) =>
        _stageScope.PublishIntermediate(stage, media, vae);

    public void Dispose() => _stageScope.Dispose();

    private RuntimeArtifact CaptureRuntimeArtifact()
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        return RuntimeArtifact.Capture(_generator, bridge);
    }

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
