using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Adapts a completed Wan stage into the host image-to-video builder's decoded-video refinement
/// contract. The host owns conditioning and latent encoding; this collaborator supplies the
/// denoise start step, verifies live decoded media, and removes the host's per-pass global trim
/// wrapper.
/// </summary>
internal sealed class WanDecodedVideoStageInput(
    WorkflowGenerator g,
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
        if (stage.Input == StageInputKind.RootMedia)
        {
            return;
        }

        WanStagePayload payload = stage.RequireWanPayload();
        int startStep = WanStageSchedulePolicy.StartStep(
            payload.Steps,
            payload.Control);
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
        if (media.GetRawFPS() != framesPerSecond
            || expectedFrames is int frames && media.Frames != frames)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} requires its {owner} at "
                + $"{expectedFrames} frames and {framesPerSecond} fps, but received "
                + $"{media.Frames} frames and {media.GetRawFPS()} fps.");
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
