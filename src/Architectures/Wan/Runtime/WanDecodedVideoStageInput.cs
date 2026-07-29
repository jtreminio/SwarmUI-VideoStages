using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Adapts a completed Wan stage into the host image-to-video builder's decoded-video refinement
/// contract. The host owns conditioning and latent encoding; this collaborator supplies the
/// first-frame selector, denoise start step, exact media invariants, and removal of its per-pass
/// global trim wrapper.
/// </summary>
internal sealed class WanDecodedVideoStageInput(
    WorkflowGenerator g,
    (int Width, int Height) dimensions,
    int framesPerSecond,
    GlobalVideoFrameTrimmer trimmer)
{
    internal void Configure(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(genInfo);
        WanStagePayload payload = stage.RequireWanPayload();
        if (stage.Input == StageInputKind.RootMedia)
        {
            if (stage.ClipStageIndex != 0 || clip.IsSourced)
            {
                throw new InvalidOperationException(
                    $"Clip {clip.ClipId} stage {stage.StageId} has unsupported Wan input "
                    + $"'{stage.Input}'.");
            }
            if (!double.IsFinite(payload.Control) || payload.Control != 1)
            {
                throw new InvalidOperationException(
                    $"Clip {clip.ClipId} stage {stage.StageId} reached the Wan runtime with "
                    + $"generated-root control '{payload.Control}' instead of 1.");
            }
            return;
        }
        ValidateDecodedStagePosition(clip, stage);

        if (!double.IsFinite(payload.Control) || payload.Control <= 0 || payload.Control > 1)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} stage {stage.StageId} reached the Wan runtime with invalid "
                + $"control '{payload.Control}'.");
        }
        int startStep = WanStageSchedulePolicy.StartStep(
            payload.Steps,
            payload.Control);
        if (WanStageSchedulePolicy.IsQuantizedZeroPartial(
                payload.Steps,
                payload.Control))
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} stage {stage.StageId} reached the Wan runtime with partial "
                + $"control '{payload.Control}' that quantizes to sampler start step 0.");
        }
        if (payload.Control < 1 && genInfo.VideoSwapModel is not null)
        {
            if (!double.IsFinite(genInfo.VideoSwapPercent)
                || genInfo.VideoSwapPercent < 0
                || genInfo.VideoSwapPercent > 1)
            {
                throw new InvalidOperationException(
                    $"Clip {clip.ClipId} stage {stage.StageId} reached the Wan runtime with "
                    + $"invalid swap percent '{genInfo.VideoSwapPercent}'.");
            }
            if (!WanStageSchedulePolicy.HasSwapHighNoiseWindow(
                    payload.Steps,
                    payload.Control,
                    genInfo.VideoSwapPercent))
            {
                int highEndStep = WanStageSchedulePolicy.HostHighEndStep(
                    payload.Steps,
                    genInfo.VideoSwapPercent);
                throw new InvalidOperationException(
                    $"Clip {clip.ClipId} stage {stage.StageId} reached the Wan runtime with no "
                    + $"high-noise sampling window: start step {startStep} must be less than "
                    + $"swap high-noise end step {highEndStep}.");
            }
        }
        ValidateDecodedInput(clip, stage, genInfo.Frames);
        genInfo.BatchIndex = 0;
        genInfo.BatchLen = 1;
        genInfo.StartStep = startStep;
    }

    internal void ConfigurePassthrough(
        ClipPlan clip,
        StagePlan stage,
        int? expectedFrames)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stage);
        WanStagePayload payload = stage.RequireWanPayload();
        ValidateDecodedStagePosition(clip, stage);
        if (!stage.IsPassthrough
            || !double.IsFinite(payload.Control)
            || payload.Control != 0)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} stage {stage.StageId} reached the Wan runtime with invalid "
                + $"passthrough control '{payload.Control}'.");
        }
        ValidateDecodedInput(clip, stage, expectedFrames);
        g.CurrentMedia.AttachedAudio = null;
    }

    internal void NormalizeDecodedOutput(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        DropHostStageTrim();
        if (g.CurrentMedia is null)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} produced no Wan video.");
        }
        g.CurrentMedia.Width = dimensions.Width;
        g.CurrentMedia.Height = dimensions.Height;
        g.CurrentMedia.Frames = genInfo.Frames ?? g.CurrentMedia.Frames;
        g.CurrentMedia.FPS = genInfo.VideoFPS ?? g.CurrentMedia.FPS;
        g.CurrentMedia.AttachedAudio = null;
        g.CurrentVae = genInfo.Vae;

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (g.CurrentMedia.DataType != WGNodeData.DT_VIDEO
            || g.CurrentMedia.Path is not JArray { Count: 2 } path
            || bridge.ResolvePath(path) is null)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} did not produce a "
                + "resolvable decoded Wan video.");
        }
    }

    private void ValidateDecodedInput(ClipPlan clip, StagePlan stage, int? expectedFrames)
    {
        WGNodeData media = g.CurrentMedia;
        string owner = stage.Input == StageInputKind.SourceVideo
            ? "conformed source video"
            : "immediately previous stage's decoded video";
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (media?.DataType != WGNodeData.DT_VIDEO
            || media.Path is not JArray { Count: 2 } path
            || bridge.ResolvePath(path) is null)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} requires its resolvable "
                + $"{owner}.");
        }
        if (media.Width != dimensions.Width
            || media.Height != dimensions.Height
            || media.GetRawFPS() != framesPerSecond
            || expectedFrames is int frames && media.Frames != frames)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} requires its {owner} at "
                + $"{dimensions.Width}x{dimensions.Height}, {expectedFrames} frames, "
                + $"{framesPerSecond} fps, but received {media.Width}x{media.Height}, "
                + $"{media.Frames} frames, {media.GetRawFPS()} fps.");
        }
    }

    private static void ValidateDecodedStagePosition(ClipPlan clip, StagePlan stage)
    {
        bool validSource =
            stage.Input == StageInputKind.SourceVideo
            && stage.ClipStageIndex == 0
            && clip.EntryMode == ArchitectureEntryMode.SourceVideo
            && clip.Input == ClipInputKind.SourceVideo
            && clip.IsSourced
            && clip.SourceVideo is not null;
        bool validPrevious =
            stage.Input == StageInputKind.PreviousStage
            && stage.ClipStageIndex > 0;
        if (!validSource && !validPrevious)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} stage {stage.StageId} has unsupported Wan input "
                + $"'{stage.Input}' at clip-stage index {stage.ClipStageIndex} for entry mode "
                + $"'{clip.EntryMode}'.");
        }
    }

    /// <summary>
    /// The host wraps every image-to-video pass in the request-global trim. A stage chain must
    /// discard each wrapper and apply that trim only once to the final stage/timeline.
    /// </summary>
    private void DropHostStageTrim()
    {
        if (!trimmer.IsRequested || g.CurrentMedia?.Path is not JArray { Count: 2 } path)
        {
            return;
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        if (bridge.NodeAt<SwarmTrimFramesNode>(path)?.Image.Connection is not INodeOutput source)
        {
            return;
        }
        string trimNodeId = $"{path[0]}";
        g.CurrentMedia = g.CurrentMedia.WithPath(source.ToPath());
        VideoGraphHelpers.RemoveNode(g, bridge, trimNodeId);
    }
}
