using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>The host media every Wan clip enters from, snapshotted once per timeline.</summary>
internal sealed record WanRootSources(WGNodeData Media, WGNodeData Vae);

/// <summary>
/// Runs one Wan clip. Graph construction stays the host's: this session resolves the compiled
/// stage settings, hands them to <see cref="WorkflowGenerator.CreateImageToVideo"/>, and reconciles
/// what that leaves behind with the timeline semantics the plan already committed to.
/// </summary>
internal sealed class WanGenerationSession(
    WorkflowGenerator g,
    VideoExecutionPlan plan,
    WanRootSources rootSources,
    WanStageHostScope hostScope) : IVideoGenerationSession
{
    private readonly PlannedStagePromptResolver _prompts = new(g);
    private readonly GlobalVideoFrameTrimmer _trimmer = new(g);

    /// <summary>
    /// The timeline resolution on the shared VideoStages pixel grid, which is already a whole
    /// multiple of Wan's own latent-and-patch requirement.
    /// </summary>
    private readonly (int Width, int Height) _dimensions =
        DimensionSnap.Snap(plan.Width, plan.Height);

    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ClipPlan clip = context.Clip;
        _ = clip.RequireWanPayload();
        // Multi-stage clips are refused during capability validation; a clip that reached the
        // runtime without exactly one stage means the two disagree.
        StagePlan stage = clip.Stages.Count == 1
            ? clip.Stages[0]
            : throw new InvalidOperationException(
                $"Clip {clip.ClipId} reached the Wan runtime with {clip.Stages.Count} stages.");

        // Every Wan clip re-enters from the host root: the slice supports hard cuts only, so no
        // clip continues from the previous clip's tail.
        g.CurrentMedia = rootSources.Media?.Duplicate()
            ?? throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} has no host image to generate from.");
        // Wan declares audio disabled. A mixed timeline's shared host image may acquire an
        // architecture-owned latent-audio attachment while another factory prepares its root;
        // it is not a decoded track Wan can carry into its neutral clip artifact.
        g.CurrentMedia.AttachedAudio = null;
        g.CurrentVae = rootSources.Vae?.Duplicate();

        int sectionId = hostScope.ApplyStageOverrides(clip, stage);
        WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(clip, stage, sectionId);
        // SwarmUI's generic builder creates latent audio whenever CurrentAudioVae is set. Another
        // architecture can leave one ambient on a mixed timeline, but Wan's declared output is
        // video-only, so isolate the call and restore the shared host value afterwards.
        WGNodeData ambientAudioVae = g.CurrentAudioVae;
        try
        {
            g.CurrentAudioVae = null;
            g.CreateImageToVideo(genInfo);
        }
        finally
        {
            g.CurrentAudioVae = ambientAudioVae;
        }
        DecodedClipArtifact output = Publish(clip, stage, genInfo);
        hostScope.PublishIntermediate(stage);
        return output;
    }

    public void Dispose() => hostScope.Dispose();

    private WorkflowGenerator.ImageToVideoGenInfo BuildGenInfo(
        ClipPlan clip,
        StagePlan stage,
        int sectionId)
    {
        WanStagePayload payload = stage.RequireWanPayload();
        T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null, sectionId: sectionId)
            ?? throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} could not resolve Wan video model "
                + $"'{payload.Model}'.");
        // The host caches its loader nodes per model name, and the core video pass this clip
        // replaces populated that cache with nodes the root handoff has since pruned. Reusing it
        // would wire the clip's sampler and text encoders to nodes that no longer exist.
        _ = g.NodeHelpers.Remove($"modelloader_{videoModel.Name}_image2video");
        (string positive, string negative) = _prompts.Resolve(clip, stage);
        return new WorkflowGenerator.ImageToVideoGenInfo
        {
            Generator = g,
            VideoModel = videoModel,
            Frames = ResolveFrames(clip, stage, sectionId),
            VideoCFG = payload.CfgScale,
            VideoFPS = plan.FramesPerSecond,
            Width = _dimensions.Width,
            Height = _dimensions.Height,
            Prompt = positive,
            NegativePrompt = negative,
            Steps = payload.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.StageId,
            ContextID = sectionId,
        };
    }

    /// <summary>
    /// An authored clip duration wins; otherwise the clip inherits the host's video length. Wan
    /// enters from a still image, so there is no source media length to fall back on. Either way
    /// the count is snapped, because Wan generates whole latent frames and an off-grid request
    /// silently yields fewer pixel frames than were asked for.
    /// </summary>
    private int? ResolveFrames(ClipPlan clip, StagePlan stage, int sectionId)
    {
        int? requested = clip.Frames is int authored && authored > 0
            ? authored
            : g.UserInput.TryGet(
                T2IParamTypes.VideoFrames,
                out int hostFrames,
                sectionId: sectionId)
                ? hostFrames
                : null;
        if (requested is not int frames)
        {
            return null;
        }
        int snapped = ((Math.Max(1, frames) - 1) / WanArchitectureModule.FrameGrid
            * WanArchitectureModule.FrameGrid) + 1;
        if (snapped != frames)
        {
            Logs.Info(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} length {frames} snapped to "
                + $"{snapped} — Wan generates in steps of {WanArchitectureModule.FrameGrid} frames.");
        }
        return snapped;
    }

    /// <summary>
    /// Reconciles the host's output with the plan's. The clip's decoded video becomes the neutral
    /// artifact assembly consumes, carrying the dimensions and length the graph actually produces.
    /// </summary>
    private DecodedClipArtifact Publish(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        DropHostClipTrim();
        g.CurrentMedia.Width = _dimensions.Width;
        g.CurrentMedia.Height = _dimensions.Height;
        g.CurrentMedia.Frames = genInfo.Frames ?? g.CurrentMedia.Frames;
        g.CurrentMedia.FPS = genInfo.VideoFPS ?? g.CurrentMedia.FPS;
        g.CurrentVae = genInfo.Vae;
        if (stage.Output.IsTimelineTerminal && _trimmer.IsRequested)
        {
            _trimmer.Apply();
        }
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        return DecodedClipArtifact.FromRuntime(
            RuntimeArtifact.Capture(g, bridge, ArtifactOrigin.StageOutput),
            clip);
    }

    /// <summary>
    /// The host trims every image-to-video pass it builds, but the global trim is a property of the
    /// finished timeline, not of each clip in it. Left in place, a multi-clip timeline would trim
    /// once per clip and then once more over the join.
    /// </summary>
    private void DropHostClipTrim()
    {
        if (!_trimmer.IsRequested || g.CurrentMedia?.Path is not JArray { Count: 2 } path)
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
