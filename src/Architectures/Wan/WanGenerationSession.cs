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
/// Runs one Wan clip. Host primitives own graph construction: this session resolves the compiled
/// stage settings, delegates image/video inputs to
/// <see cref="WorkflowGenerator.CreateImageToVideo"/>, composes the same public primitives for a
/// native WAN text latent, and reconciles the result with the committed timeline semantics.
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
    private readonly StagePixelScaleGraphBuilder _pixelScaler = new(g);

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
        else if (clip.Input == ClipInputKind.EmptyLatent)
        {
            // Native WAN text entry asks the host for an empty video after loading the authored
            // model. The discarded host text donor is never decoded or treated as an image.
            g.CurrentMedia = null;
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
        if (g.CurrentMedia is not null)
        {
            g.CurrentMedia.AttachedAudio = null;
        }

        WanDecodedVideoStageInput stageInput = new(
            g,
            plan.FramesPerSecond,
            _trimmer);
        foreach (StagePlan stage in clip.Stages)
        {
            ApplyPixelUpscale(stage);
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
            string stageLoaderKey = $"modelloader_{payload.Model}_image2video";
            bool transientStageLoader =
                promptLoraScope is not null
                || !payload.Loras.IsDefaultOrEmpty;
            // The host cache key does not encode the active LoRA parameter scope. Always make
            // the ordinary stage reload under its effective plan, including an
            // empty plan after a prior scoped stage. Existing graph nodes stay live.
            VideoGraphHelpers.RemoveCached(g, stageLoaderKey);
            try
            {
                WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(
                    clip,
                    stage,
                    sectionId);
                if (stage.Input == StageInputKind.EmptyLatent)
                {
                    ExecuteNativeTextStage(clip, stage, genInfo);
                    stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
                    return;
                }
                stageInput.Configure(clip, stage, genInfo);
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
                // Removing the marker never prunes live nodes.
                if (transientStageLoader)
                {
                    VideoGraphHelpers.RemoveCached(g, stageLoaderKey);
                }
            }
        }
    }

    /// <summary>
    /// Native WAN text stage-zero construction. Host primitives own model loading, conditioning,
    /// empty-video creation, and sampling. The narrow parameter scope supplies the authored stage
    /// length to the host primitive without changing the request retained in metadata.
    /// </summary>
    private void ExecuteNativeTextStage(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WanStagePayload payload = stage.RequireWanPayload();
        if (clip.EntryMode != ArchitectureEntryMode.TextToVideo
            || clip.Input != ClipInputKind.EmptyLatent
            || stage.ClipStageIndex != 0
            || genInfo.VideoEndFrame is not null)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} stage {stage.StageId} has an invalid native Wan text "
                    + "execution contract.");
        }

        WGNodeData ambientAudioVae = g.CurrentAudioVae;
        bool ambientImageToVideo = g.IsImageToVideo;
        try
        {
            g.CurrentAudioVae = null;
            // Native text entry bypasses the host image-to-video wrapper, but its model,
            // conditioning, sampler, and decode are still the video generation section.
            // Keep the host's confinement selector accurate for this complete scope.
            g.IsImageToVideo = true;
            genInfo.PrepModelAndCond(g);
            int frames = genInfo.Frames
                ?? throw new InvalidOperationException(
                    $"Clip {clip.ClipId} stage {stage.StageId} has no native Wan frame count.");
            WGNodeData ambientVae = g.CurrentVae;
            using (ParamSnapshot frameScope = ParamSnapshot.Of(
                g.UserInput,
                T2IParamTypes.Text2VideoFrames.Type))
            {
                try
                {
                    g.UserInput.Set(T2IParamTypes.Text2VideoFrames, frames);
                    g.CurrentVae = genInfo.Vae;
                    g.CurrentMedia = g.EmptyImage(
                        (int)genInfo.Width,
                        (int)genInfo.Height,
                        1);
                    ValidateNativeTextLatent(
                        clip,
                        stage,
                        g.CurrentMedia,
                        (int)genInfo.Width,
                        (int)genInfo.Height,
                        frames);
                }
                finally
                {
                    g.CurrentVae = ambientVae;
                }
            }
            g.CurrentMedia.FPS = plan.FramesPerSecond;
            string sampled = g.CreateKSampler(
                genInfo.Model.Path,
                genInfo.PosCond,
                genInfo.NegCond,
                g.CurrentMedia.Path,
                payload.CfgScale,
                payload.Steps,
                startStep: 0,
                endStep: 10000,
                seed: genInfo.Seed,
                returnWithLeftoverNoise: false,
                addNoise: true,
                sigmin: 0.002,
                sigmax: 1000,
                defsampler: "euler",
                defscheduler: "simple",
                explicitSampler: payload.Sampler,
                explicitScheduler: payload.Scheduler,
                sectionId: genInfo.ContextID);
            g.CurrentMedia = g.CurrentMedia
                .WithPath([sampled, 0])
                .AsRawImage(genInfo.Vae);
        }
        finally
        {
            g.CurrentAudioVae = ambientAudioVae;
            g.IsImageToVideo = ambientImageToVideo;
        }
    }

    private void ValidateNativeTextLatent(
        ClipPlan clip,
        StagePlan stage,
        WGNodeData latent,
        int width,
        int height,
        int frames)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        bool valid =
            latent is not null
            && latent.DataType == WGNodeData.DT_LATENT_VIDEO
            && latent.Path is Newtonsoft.Json.Linq.JArray { Count: 2 } path
            && bridge.ResolvePath(path) is not null
            && latent.Frames == frames
            && latent.Width == width
            && latent.Height == height;
        if (!valid)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} could not create a "
                    + $"valid {width}x{height}, {frames}-frame WAN text-video latent.");
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
        int width = g.CurrentMedia?.Width ?? _dimensions.Width;
        int height = g.CurrentMedia?.Height ?? _dimensions.Height;
        return new WorkflowGenerator.ImageToVideoGenInfo
        {
            Generator = g,
            VideoModel = videoModel,
            // High- and low-noise WAN models are ordinary authored stages. Never let the host
            // append its request-global legacy swap pass to a stage.
            VideoSwapModel = null,
            VideoSwapPercent = 0.5,
            Frames = ResolveFrames(clip, stage, sectionId),
            VideoCFG = payload.CfgScale,
            VideoFPS = plan.FramesPerSecond,
            Width = width,
            Height = height,
            Prompt = positive,
            NegativePrompt = negative,
            Steps = payload.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.StageId,
            ContextID = sectionId,
            VideoEndFrame = WanVideoEndFramePolicy.ShouldApply(plan, clip, stage)
                ? g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
                : null,
        };
    }

    private void ApplyPixelUpscale(StagePlan stage)
    {
        StageUpscalePlan upscale = stage.RequireWanPayload().Upscale;
        if (upscale.Mode == StageUpscaleMode.None)
        {
            return;
        }
        if (upscale.Mode != StageUpscaleMode.Pixel)
        {
            throw new InvalidOperationException(
                $"Clip stage {stage.StageId} reached the Wan runtime with unsupported upscale "
                    + $"method '{upscale.RawMethod}'.");
        }
        if (g.CurrentMedia is null)
        {
            // Native text entry has no pixels to resize. Its generated dimensions remain the
            // authored timeline dimensions, matching the existing LTX text-entry behavior.
            return;
        }

        int currentWidth = g.CurrentMedia.Width
            ?? throw new InvalidOperationException(
                $"Clip stage {stage.StageId} cannot pixel-scale media with no width.");
        int currentHeight = g.CurrentMedia.Height
            ?? throw new InvalidOperationException(
                $"Clip stage {stage.StageId} cannot pixel-scale media with no height.");
        (int targetWidth, int targetHeight) = DimensionSnap.Snap(
            currentWidth * upscale.Factor,
            currentHeight * upscale.Factor);
        _pixelScaler.Apply(
            g.CurrentMedia,
            targetWidth,
            targetHeight,
            upscale.MethodName);
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
        else if (clip.EntryMode == ArchitectureEntryMode.TextToVideo)
        {
            requested = stage.Input == StageInputKind.EmptyLatent
                ? g.UserInput.Get(T2IParamTypes.Text2VideoFrames, 81)
                : g.CurrentMedia?.Frames;
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
