using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Graph;
using VideoStages.Planning;

namespace VideoStages.Execution.StockHost;

/// <summary>
/// Adapts a completed decoded stage into the host image-to-video builder's refinement contract.
/// The host owns conditioning and latent encoding; this collaborator supplies the caller's
/// denoise start step, verifies live decoded media, and removes the host's per-pass global trim
/// wrapper.
/// </summary>
internal sealed class HostVideoDecodedStageInput
{
    private readonly WorkflowGenerator _generator;
    private readonly int _framesPerSecond;
    private readonly string _architectureDisplayLabel;
    /// <summary>Only an architecture whose own output carries decoded audio may keep it.</summary>
    private readonly bool _preserveAttachedAudio;
    private JArray _hostSourcePath;
    private int? _hostSourceFrames;

    internal HostVideoDecodedStageInput(
        WorkflowGenerator generator,
        int framesPerSecond,
        string architectureDisplayLabel,
        bool preserveAttachedAudio)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrWhiteSpace(architectureDisplayLabel);
        _generator = generator;
        _framesPerSecond = framesPerSecond;
        _architectureDisplayLabel = architectureDisplayLabel;
        _preserveAttachedAudio = preserveAttachedAudio;
    }

    internal void Configure(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int startStep)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(genInfo);
        if (stage.Input == StageInputKind.RootMedia)
        {
            return;
        }

        ValidateDecodedInput(clip, stage, genInfo.Frames);
        _hostSourcePath = _generator.CurrentMedia.Path;
        _hostSourceFrames = genInfo.Frames;
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
        if (!_preserveAttachedAudio)
        {
            _generator.CurrentMedia.AttachedAudio = null;
        }
    }

    internal void NormalizeDecodedOutput(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        DropHostStageTrim();
        WidenHostSourceClamp();
        if (_generator.CurrentMedia is null)
        {
            throw Invariant.Failure(
                $"clip {clip.ClipId} stage {stage.StageId} produced no "
                + $"{_architectureDisplayLabel} video.");
        }
        _generator.CurrentMedia.Frames =
            genInfo.Frames ?? _generator.CurrentMedia.Frames;
        _generator.CurrentMedia.FPS =
            genInfo.VideoFPS ?? _generator.CurrentMedia.FPS;
        if (!_preserveAttachedAudio)
        {
            _generator.CurrentMedia.AttachedAudio = null;
        }
        _generator.CurrentVae = genInfo.Vae;

        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        if (_generator.CurrentMedia.DataType != WGNodeData.DT_VIDEO
            || _generator.CurrentMedia.Path is not JArray { Count: 2 } path
            || bridge.ResolvePath(path) is null)
        {
            throw Invariant.Failure(
                $"clip {clip.ClipId} stage {stage.StageId} did not produce a "
                + $"resolvable decoded {_architectureDisplayLabel} video.");
        }
    }

    /// <summary>
    /// The host builds its sampling latent from a still, so it wraps its source media in an
    /// <c>ImageFromBatch</c> that keeps one frame. This stage handed it a whole video already
    /// conformed to the stage length, which that wrapper would decimate, so the kept length is
    /// widened back. The host reaches its own start image through a second <c>ImageFromBatch</c>
    /// downstream of this one, so conditioning still sees frame 0 alone.
    /// </summary>
    private void WidenHostSourceClamp()
    {
        JArray sourcePath = _hostSourcePath;
        int? frames = _hostSourceFrames;
        _hostSourcePath = null;
        _hostSourceFrames = null;
        if (sourcePath is null
            || frames is not > 1
            || FindNodeReading("ImageScale", sourcePath) is not string scaled
            || FindNodeReading("ImageFromBatch", [scaled, 0]) is not string clamp
            || _generator.Workflow[clamp]?["inputs"] is not JObject inputs
            || inputs["length"]?.Value<int>() != 1)
        {
            return;
        }
        inputs["length"] = frames;
        // The host caches generic nodes by their inputs, which no longer describe this one.
        VideoGraphHelpers.InvalidateForRemovedNodes(_generator.NodeHelpers, [clamp]);
    }

    /// <summary>The id of the node of <paramref name="classType"/> whose 'image' input is
    /// <paramref name="imagePath"/>, or null when the host built no such node.</summary>
    private string FindNodeReading(string classType, JArray imagePath) =>
        _generator.Workflow.Properties()
            .FirstOrDefault(property =>
                property.Value is JObject node
                && $"{node["class_type"]}" == classType
                && VideoGraphHelpers.TryGetInputRef(node, "image", out JArray image)
                && JToken.DeepEquals(image, imagePath))
            ?.Name;

    private void ValidateDecodedInput(ClipPlan clip, StagePlan stage, int? expectedFrames)
    {
        WGNodeData media = _generator.CurrentMedia;
        string owner = stage.Input == StageInputKind.InitVideo
            ? "conformed source video"
            : "immediately previous stage's decoded video";
        using WorkflowBridge bridge = WorkflowBridge.Create(_generator.Workflow);
        if (media?.DataType != WGNodeData.DT_VIDEO
            || media.Path is not JArray { Count: 2 } path
            || bridge.ResolvePath(path) is null)
        {
            throw Invariant.Failure(
                $"clip {clip.ClipId} stage {stage.StageId} requires its resolvable "
                + $"{owner}.");
        }
        if (media.GetRawFPS() != _framesPerSecond
            || expectedFrames is int frames && media.Frames != frames)
        {
            throw Invariant.Failure(
                $"clip {clip.ClipId} stage {stage.StageId} requires its {owner} at "
                + $"{expectedFrames} frames and {_framesPerSecond} fps, but received "
                + $"{media.Frames} frames and {media.GetRawFPS()} fps.");
        }
    }

    /// <summary>
    /// The host wraps every image-to-video pass in the request-global trim. A stage chain must
    /// discard each wrapper and apply that trim only once to the final stage/timeline.
    /// </summary>
    private void DropHostStageTrim()
    {
        if (!Timeline.GlobalVideoFrameTrimmer.IsRequested(_generator.UserInput)
            || _generator.CurrentMedia?.Path is not JArray { Count: 2 } path)
        {
            return;
        }
        using WorkflowBridge bridge = BridgeSync.For(_generator);
        if (bridge.NodeAt<SwarmTrimFramesNode>(path)?.Image.Connection is not INodeOutput source)
        {
            return;
        }
        string trimNodeId = $"{path[0]}";
        _generator.CurrentMedia = _generator.CurrentMedia.WithPath(source.ToPath());
        VideoGraphHelpers.RemoveNode(_generator, bridge, trimNodeId);
    }
}
