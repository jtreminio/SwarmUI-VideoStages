using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Execution;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>The host media every WAN clip enters from, snapshotted once per timeline.</summary>
internal sealed record WanRootSources(WGNodeData Media, WGNodeData Vae);

/// <summary>
/// Runs one WAN clip. Host primitives own graph construction: this session resolves the compiled
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
    private readonly WanFrameReferenceResolver _frameReferences = new(g);

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
                    $"Sourced WAN clip {clip.ClipId} has no source-video plan.");
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
            WGNodeData authoredFirst = _frameReferences.ResolveFirst(
                clip.RequireWanPayload().FirstFrameReference);
            if (authoredFirst is not null)
            {
                g.CurrentMedia = authoredFirst;
                // The selected WAN model supplies its own VAE during host preparation. Do not
                // attach an unrelated text-root donor VAE to the uploaded image.
                g.CurrentVae = null;
            }
            else if (clip.Input == ClipInputKind.EmptyLatent)
            {
                // Upload materialization is deliberately runtime-owned. A missing or malformed
                // first image leaves the text-root plan unchanged and falls back to native text
                // generation without borrowing a host image.
                g.CurrentMedia = null;
                g.CurrentVae = null;
            }
            else
            {
                // Every other generated Wan clip re-enters from the host root: the slice supports
                // hard cuts only, so no clip continues from the previous clip's tail.
                g.CurrentMedia = rootSources.Media?.Duplicate()
                    ?? throw new SwarmUserErrorException(
                        $"VideoStages: clip {clip.ClipId} has no host image to generate from.");
                g.CurrentVae = rootSources.Vae?.Duplicate();
            }
        }
        // Wan declares audio disabled. The sourced path does not build an audio branch, and a
        // mixed timeline's shared host image may still acquire an architecture-owned attachment.
        // Neither can become a decoded track on Wan's neutral clip artifact.
        if (g.CurrentMedia is not null)
        {
            g.CurrentMedia.AttachedAudio = null;
        }

        HostVideoDecodedStageInput stageInput = new(
            g,
            plan.FramesPerSecond,
            _trimmer,
            "Wan");
        foreach (StagePlan stage in clip.Stages)
        {
            ApplyPixelUpscale(stage);
            if (stage.IsPassthrough)
            {
                stageInput.ConfigurePassthrough(
                    clip,
                    stage,
                    stage.Input == StageInputKind.PreviousStage
                        ? g.CurrentMedia?.Frames
                        : ResolveRequestedFrames(clip, stage, sectionId: null));
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
        HostVideoDecodedStageInput stageInput)
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
                bool materializedFirstFrame =
                    stage.Input == StageInputKind.EmptyLatent
                    && g.CurrentMedia is not null;
                if (stage.Input == StageInputKind.EmptyLatent
                    && !materializedFirstFrame)
                {
                    ExecuteNativeTextStage(clip, stage, genInfo);
                    stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
                    return;
                }
                int startStep = stage.Input == StageInputKind.RootMedia
                    ? 0
                    : WanStageSchedulePolicy.StartStep(
                        payload.Steps,
                        payload.Control);
                if (!materializedFirstFrame)
                {
                    stageInput.Configure(clip, stage, genInfo, startStep);
                }
                // SwarmUI's generic builder creates latent audio whenever CurrentAudioVae is
                // set. Another architecture can leave one ambient on a mixed timeline, but
                // Wan's declared output is video-only, so isolate every pass and restore the
                // shared host value.
                WGNodeData ambientAudioVae = g.CurrentAudioVae;
                HashSet<string> preHostNodeIds = null;
                if (string.Equals(
                        payload.ModelClassId,
                        WanArchitectureModule.Ti2v5bModelClassId,
                        StringComparison.OrdinalIgnoreCase)
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
            || stage.ClipStageIndex != 0)
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
            if (genInfo.VideoEndFrame is not null)
            {
                BuildNativeLastFrameConditioning(genInfo, frames);
            }
            else
            {
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

    private void BuildNativeLastFrameConditioning(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int frames)
    {
        WGNodeData endFrame = g.LoadImage(
            genInfo.VideoEndFrame,
            "${videostageswanlastframe}",
            false);
        string scaled = g.CreateNode("ImageScale", new Newtonsoft.Json.Linq.JObject
        {
            ["image"] = endFrame.Path,
            ["width"] = genInfo.Width,
            ["height"] = genInfo.Height,
            ["upscale_method"] = "lanczos",
            ["crop"] = "disabled",
        });
        Newtonsoft.Json.Linq.JArray scaledEnd = [scaled, 0];
        Newtonsoft.Json.Linq.JToken clipVisionEnd = null;
        string compatibilityId = genInfo.VideoModel.ModelClass?.CompatClass?.ID;
        bool exactWan22ImageModel = StringUtils.Equals(
            genInfo.VideoModel.ModelClass?.ID,
            WanArchitectureModule.ImageToVideoModelClassId);
        if (!exactWan22ImageModel
            && (compatibilityId == T2IModelClassSorter.CompatWan21_14b.ID
                || compatibilityId == T2IModelClassSorter.CompatWan21_1_3b.ID))
        {
            string targetName = g.RequireVisionModel(
                "clip_vision_h.safetensors",
                "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/"
                    + "split_files/clip_vision/clip_vision_h.safetensors",
                "64a7ef761bfccbadbaa3da77366aac4185a6c58fa5de5f589b42a65bcc21f161",
                T2IParamTypes.ClipVisionModel);
            string clipLoader = g.CreateNode(
                "CLIPVisionLoader",
                new Newtonsoft.Json.Linq.JObject
                {
                    ["clip_name"] = targetName,
                });
            string encodedEnd = g.CreateNode(
                "CLIPVisionEncode",
                new Newtonsoft.Json.Linq.JObject
                {
                    ["clip_vision"] = new Newtonsoft.Json.Linq.JArray(clipLoader, 0),
                    ["image"] = scaledEnd,
                    ["crop"] = "center",
                });
            clipVisionEnd = new Newtonsoft.Json.Linq.JArray(encodedEnd, 0);
        }
        string conditioning = g.CreateNode(
            "WanFirstLastFrameToVideo",
            new Newtonsoft.Json.Linq.JObject
            {
                ["width"] = genInfo.Width,
                ["height"] = genInfo.Height,
                ["length"] = frames,
                ["positive"] = genInfo.PosCond,
                ["negative"] = genInfo.NegCond,
                ["vae"] = genInfo.Vae.Path,
                ["start_image"] = null,
                ["end_image"] = scaledEnd,
                ["clip_vision_start_image"] = null,
                ["clip_vision_end_image"] = clipVisionEnd,
                ["batch_size"] = 1,
            });
        genInfo.PosCond = [conditioning, 0];
        genInfo.NegCond = [conditioning, 1];
        g.CurrentMedia = new(
            [conditioning, 2],
            g,
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat)
        {
            Width = (int)genInfo.Width,
            Height = (int)genInfo.Height,
            Frames = frames,
            FPS = plan.FramesPerSecond,
        };
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
            VideoEndFrame = ResolveEndFrame(clip, stage),
        };
    }

    private Image ResolveEndFrame(ClipPlan clip, StagePlan stage)
    {
        bool terminalGenerating = ReferenceEquals(
            stage,
            clip.Stages.LastOrDefault(candidate => !candidate.IsPassthrough));
        if (!terminalGenerating)
        {
            return null;
        }
        WanFrameReferencePlan authored = clip.RequireWanPayload().LastFrameReference;
        if (authored is not null)
        {
            return _frameReferences.ResolveLast(authored);
        }
        return WanVideoEndFramePolicy.ShouldApply(plan, clip, stage)
            ? g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
            : null;
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
    /// An authored clip duration wins; otherwise a stage inherits the applicable host video length.
    /// Passthrough stages consume this structural count directly; only generating stages project it
    /// onto the resolved model handler's temporal grid.
    /// </summary>
    private int? ResolveRequestedFrames(
        ClipPlan clip,
        StagePlan stage,
        int? sectionId)
    {
        if (clip.Frames is int authored && authored > 0)
        {
            return authored;
        }
        if (clip.EntryMode == ArchitectureEntryMode.TextToVideo)
        {
            return stage.Input == StageInputKind.EmptyLatent
                ? g.UserInput.Get(T2IParamTypes.Text2VideoFrames, 81)
                : g.CurrentMedia?.Frames;
        }
        if (sectionId is int scopedSection)
        {
            return g.UserInput.TryGet(
                T2IParamTypes.VideoFrames,
                out int scopedFrames,
                sectionId: scopedSection)
                    ? scopedFrames
                    : null;
        }
        return g.UserInput.TryGet(
            T2IParamTypes.VideoFrames,
            out int hostFrames)
                ? hostFrames
                : null;
    }

    private int? ResolveFrames(ClipPlan clip, StagePlan stage, int? sectionId)
    {
        int? requested = ResolveRequestedFrames(clip, stage, sectionId);
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
