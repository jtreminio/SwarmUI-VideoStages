using ComfyTyped.Core;
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
    private readonly SourcedClipInstaller _sourcedClipInstaller = new(g);

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
        WanRuntimeClipContract.Validate(plan, clip);

        if (clip.IsSourced)
        {
            SourceVideoPlan source = clip.SourceVideo
                ?? throw new InvalidOperationException(
                    $"Sourced Wan clip {clip.ClipId} has no source-video plan.");
            ClipPlan sourceInstallPlan = clip with
            {
                SourceVideo = source with
                {
                    TargetWidth = _dimensions.Width,
                    TargetHeight = _dimensions.Height,
                },
            };
            g.CurrentMedia = _sourcedClipInstaller.TryInstall(
                sourceInstallPlan,
                includeSourceAudio: false)
                ?? throw new SwarmUserErrorException(
                    $"VideoStages: clip {clip.ClipId} source video could not be installed.");
            g.CurrentVae = null;
        }
        else
        {
            // Every generated Wan clip re-enters from the host root: the slice supports hard cuts
            // only, so no clip continues from the previous clip's tail.
            g.CurrentMedia = rootSources.Media?.Duplicate()
                ?? throw new SwarmUserErrorException(
                    $"VideoStages: clip {clip.ClipId} has no host image to generate from.");
            g.CurrentVae = rootSources.Vae?.Duplicate();
        }
        // Wan declares audio disabled. The sourced path does not build an audio branch, and a
        // mixed timeline's shared host image may still acquire an architecture-owned attachment.
        // Neither can become a decoded track on Wan's neutral clip artifact.
        g.CurrentMedia.AttachedAudio = null;

        WanDecodedVideoStageInput stageInput = new(
            g,
            _dimensions,
            plan.FramesPerSecond,
            _trimmer);
        foreach (StagePlan stage in clip.Stages)
        {
            if (stage.IsPassthrough)
            {
                stageInput.ConfigurePassthrough(
                    clip,
                    stage,
                    ResolveFrames(clip, stage, sectionId: null));
                hostScope.PublishIntermediate(stage);
                continue;
            }
            ExecuteGeneratingStage(clip, stage, stageInput);
            hostScope.PublishIntermediate(stage);
        }
        StagePlan finalStage = clip.Stages[^1];
        if (finalStage.Output.IsTimelineTerminal && _trimmer.IsRequested)
        {
            _trimmer.Apply();
        }
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        return DecodedClipArtifact.FromRuntime(
            RuntimeArtifact.Capture(g, bridge, ArtifactOrigin.StageOutput),
            clip);
    }

    public void Dispose() => hostScope.Dispose();

    private void ExecuteGeneratingStage(
        ClipPlan clip,
        StagePlan stage,
        WanDecodedVideoStageInput stageInput)
    {
        WanStagePayload payload = stage.RequireWanPayload();
        int sectionId = hostScope.ApplyStageOverrides(clip, stage);
        using (ParamSnapshot promptLoraScope = PromptParser.ApplyLoraScope(
            g.UserInput,
            clip.ClipId,
            sectionId,
            NormalLoraTargetPolicy.ModelOnly))
        using (ParamSnapshot loraScope =
            LoraParams.ApplyNormalLoras(g.UserInput, payload.Loras))
        {
            string highLoaderKey = $"modelloader_{payload.Model}_image2video";
            T2IModel swapModel = g.UserInput.Get(T2IParamTypes.VideoSwapModel, null);
            bool transientHighLoader =
                promptLoraScope is not null
                || !payload.Loras.IsDefaultOrEmpty
                || swapModel is not null
                    && string.Equals(
                        swapModel.Name,
                        payload.Model,
                        StringComparison.Ordinal);
            // The host cache key does not encode the active LoRA parameter scope. Always make
            // the high-noise branch reload under this stage's effective plan, including an
            // empty plan after a prior scoped stage. Existing graph nodes stay live.
            VideoGraphHelpers.RemoveCached(g, highLoaderKey);
            try
            {
                WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(
                    clip,
                    stage,
                    sectionId);
                stageInput.Configure(clip, stage, genInfo);
                using IDisposable sameModelSwapCacheScope =
                    transientHighLoader
                    && genInfo.VideoSwapModel is not null
                    && string.Equals(
                        genInfo.VideoSwapModel.Name,
                        payload.Model,
                        StringComparison.Ordinal)
                        ? AltImageToVideoScope.Post(
                            genInfo,
                            _ => VideoGraphHelpers.RemoveCached(g, highLoaderKey))
                        : null;
                // SwarmUI's generic builder creates latent audio whenever CurrentAudioVae is
                // set. Another architecture can leave one ambient on a mixed timeline, but
                // Wan's declared output is video-only, so isolate every pass and restore the
                // shared host value.
                WGNodeData ambientAudioVae = g.CurrentAudioVae;
                HashSet<string> preHostNodeIds = null;
                if (payload.ProfileId == WanArchitectureModule.Ti2v5bProfileId
                    && genInfo.StartStep > 0)
                {
                    preHostNodeIds = [
                        .. g.Workflow.Properties().Select(property => property.Name),
                    ];
                }
                Exception hostConstructionError = null;
                try
                {
                    g.CurrentAudioVae = null;
                    g.CreateImageToVideo(genInfo);
                }
                catch (Exception error)
                {
                    hostConstructionError = error;
                    throw;
                }
                finally
                {
                    g.CurrentAudioVae = ambientAudioVae;
                    if (preHostNodeIds is not null)
                    {
                        RunPostHostCleanup(
                            () => PruneUnusedWan22Latents(preHostNodeIds),
                            hostConstructionError);
                    }
                }
                stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
            }
            finally
            {
                // A tuple built under temporary stage LoRAs cannot outlive their ParamSnapshot.
                // A same-model swap tuple is low-section state under the shared high key and is
                // transient for the same reason. Removing the marker never prunes live nodes.
                if (transientHighLoader)
                {
                    VideoGraphHelpers.RemoveCached(g, highLoaderKey);
                }
            }
        }
    }

    /// <summary>
    /// Cleanup failures are authoritative after successful host construction. While a host
    /// exception is already unwinding, cleanup is best-effort so it cannot replace that failure.
    /// </summary>
    internal static void RunPostHostCleanup(
        Action cleanup,
        Exception hostConstructionError)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            cleanup();
        }
        catch (Exception cleanupError) when (hostConstructionError is not null)
        {
            Logs.Warning(
                "VideoStages: failed to prune an unused Wan 5B latent while "
                + $"preserving the original host construction failure: "
                + $"{cleanupError.Message}");
        }
    }

    /// <summary>
    /// The host's 5B preparation always emits its native latent, then intentionally replaces that
    /// latent with a VAE encoding when partial refinement starts after step zero. Remove only
    /// consumerless nodes created by this pass. The complete pre-host graph is protected so the
    /// recursive upstream walk cannot cross from a newly removed consumer into the stage's input
    /// media, an earlier stage, or any other pre-existing branch.
    /// </summary>
    private void PruneUnusedWan22Latents(ISet<string> preHostNodeIds)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        string[] unused = [
            .. bridge.Graph.Nodes.Values
                .Where(node =>
                    node.ClassTypeName == "Wan22ImageToVideoLatent"
                    && !preHostNodeIds.Contains(node.Id)
                    && !bridge.Graph.FindInputsConnectedTo(node.FindOutput(0)).Any())
                .Select(node => node.Id),
        ];
        foreach (string nodeId in unused)
        {
            WorkflowGraphCleanup.RemoveUnusedUpstreamNodes(
                bridge,
                nodeId,
                protectedNodeIds: preHostNodeIds,
                nodeHelpers: g.NodeHelpers);
        }
    }

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
        (string positive, string negative) = _prompts.Resolve(clip, stage);
        return new WorkflowGenerator.ImageToVideoGenInfo
        {
            Generator = g,
            VideoModel = videoModel,
            VideoSwapModel = g.UserInput.Get(T2IParamTypes.VideoSwapModel, null),
            VideoSwapPercent = g.UserInput.Get(T2IParamTypes.VideoSwapPercent, 0.5),
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
            VideoEndFrame = payload.OwnsVideoEndFrame
                ? g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
                : null,
        };
    }

    /// <summary>
    /// An authored clip duration wins; otherwise a generated clip inherits the host video length.
    /// Sourced clips always carry an authored frame window from the source plan. Either way the
    /// count is snapped, because Wan generates whole latent frames and an off-grid request silently
    /// yields fewer pixel frames than were asked for.
    /// </summary>
    private int? ResolveFrames(ClipPlan clip, StagePlan stage, int? sectionId)
    {
        int? requested;
        if (clip.Frames is int authored && authored > 0)
        {
            requested = authored;
        }
        else if (sectionId is int scopedSection)
        {
            requested = g.UserInput.TryGet(
                T2IParamTypes.VideoFrames,
                out int scopedFrames,
                sectionId: scopedSection)
                    ? scopedFrames
                    : null;
        }
        else
        {
            requested = g.UserInput.TryGet(
                T2IParamTypes.VideoFrames,
                out int hostFrames)
                    ? hostFrames
                    : null;
        }
        if (requested is not int frames)
        {
            return null;
        }
        WanStaticGeneratedFrameResolution resolution =
            WanStaticGeneratedFrameResolver.Resolve(
                frames,
                clip.ClipId,
                stage.StageId,
                stage.ResolvedModel);
        int snapped = resolution.Frames;
        if (snapped != frames)
        {
            Logs.Info(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} length {frames} snapped to "
                + $"{snapped} — Wan generates in steps of {resolution.FrameGrid} frames.");
        }
        return snapped;
    }
}
