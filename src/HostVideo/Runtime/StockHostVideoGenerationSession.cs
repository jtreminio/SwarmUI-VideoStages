using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.HostVideo.Runtime;

/// <summary>
/// Runs architectures whose stages delegate to SwarmUI's stock video builders. The common
/// lifecycle stays linear here; the few WAN-specific branches cover frame references, temporal
/// snapping, native final-frame conditioning, and 5B graph cleanup.
/// </summary>
internal sealed class StockHostVideoGenerationSession(
    WorkflowGenerator g,
    VideoExecutionPlan plan,
    HostVideoRootSources rootSources,
    HostVideoStageEngine stageEngine,
    ArchitectureId architectureId,
    string architectureLabel) : IVideoGenerationSession
{
    private readonly PlannedStagePromptResolver _prompts = new(g);
    private readonly SourcedClipInstaller _sourcedClipInstaller = new(g);
    private readonly WanFrameReferenceResolver _frameReferences = new(g);

    /// <summary>The timeline resolution on the shared VideoStages pixel grid.</summary>
    private readonly (int Width, int Height) _dimensions =
        DimensionSnap.Snap(plan.Width, plan.Height);

    public ArchitectureId ArchitectureId => architectureId;

    private bool IsWan => ArchitectureId == WanArchitectureModule.ArchitectureId;

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ClipPlan clip = context.Clip;

        if (clip.IsSourced)
        {
            SourceVideoPlan source = clip.SourceVideo
                ?? throw new InvalidOperationException(
                    $"Sourced {architectureLabel} clip {clip.ClipId} has no source-video plan.");
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
            WGNodeData authoredFirst = IsWan
                ? _frameReferences.ResolveFirst(
                    clip.RequireWanPayload().FirstFrameReference)
                : null;
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
                g.CurrentMedia = rootSources.Media?.Duplicate()
                    ?? throw new SwarmUserErrorException(
                        $"VideoStages: clip {clip.ClipId} has no host image to generate from.");
                g.CurrentVae = rootSources.Vae?.Duplicate();
            }
        }
        // Both stock-host architecture descriptors currently declare audio disabled. A mixed
        // timeline's shared root may still carry another architecture's attachment.
        if (g.CurrentMedia is not null)
        {
            g.CurrentMedia.AttachedAudio = null;
        }

        return stageEngine.Execute(
            clip,
            stage => stage.Core,
            IsWan ? ResolveWanPassthroughFrames : ResolveGenericFrames,
            ExecuteGeneratingStage);
    }

    public void Dispose() => stageEngine.Dispose();

    private void ExecuteGeneratingStage(
        ClipPlan clip,
        StagePlan stage,
        HostVideoDecodedStageInput stageInput,
        int sectionId)
    {
        StockHostVideoStagePayload payload = ResolvePayload(stage);
        StageCorePlan core = stage.Core;
        using (ParamSnapshot promptLoraScope = PromptParser.ApplyLoraScope(
            g.UserInput,
            clip.ClipId,
            sectionId,
            payload.LoraTargetPolicy))
        using (ParamSnapshot loraScope =
            LoraParams.ApplyNormalLoras(g.UserInput, core.Loras))
        using (ParamSnapshot ignoredAudioReference = IsWan
            ? null
            : ParamSnapshot.Of(
                g.UserInput,
                T2IParamTypes.VideoAudioReference.Type))
        {
            if (!IsWan)
            {
                // LTX v2's stock branch reads this request-global enhancement directly. The
                // generic fallback does not advertise it, so retain it only in request metadata.
                g.UserInput.InternalSet.ValuesInput.Remove(
                    T2IParamTypes.VideoAudioReference.Type.ID);
            }
            string stageLoaderKey =
                $"modelloader_{stage.ResolvedModel.ModelName}_image2video";
            bool transientStageLoader =
                promptLoraScope is not null
                || !core.Loras.IsDefaultOrEmpty;
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
                    IsWan
                    && stage.Input == StageInputKind.EmptyLatent
                    && g.CurrentMedia is not null;
                if (stage.Input == StageInputKind.EmptyLatent
                    && !materializedFirstFrame)
                {
                    ExecuteTextStage(clip, stage, genInfo);
                    stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
                    return;
                }
                int startStep = stage.Input == StageInputKind.RootMedia
                    ? 0
                    : HostVideoStageSchedulePolicy.StartStep(
                        core.Steps,
                        core.Control);
                if (!materializedFirstFrame)
                {
                    stageInput.Configure(clip, stage, genInfo, startStep);
                }
                // SwarmUI's generic builder creates latent audio whenever CurrentAudioVae is
                // set. Stock-host stages advertise video-only output, so isolate every pass.
                WGNodeData ambientAudioVae = g.CurrentAudioVae;
                HashSet<string> preHostNodeIds = null;
                if (IsWan
                    && string.Equals(
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
                        RunWanPostHostCleanup(
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

    private void ExecuteTextStage(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        StageCorePlan core = stage.Core;
        if (clip.EntryMode != ArchitectureEntryMode.TextToVideo
            || clip.Input != ClipInputKind.EmptyLatent
            || stage.ClipStageIndex != 0)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} stage {stage.StageId} has an invalid "
                    + $"{architectureLabel} text "
                    + "execution contract.");
        }

        WGNodeData ambientAudioVae = g.CurrentAudioVae;
        bool ambientImageToVideo = g.IsImageToVideo;
        try
        {
            if (IsWan)
            {
                g.CurrentAudioVae = null;
            }
            g.IsImageToVideo = true;
            int frames = genInfo.Frames
                ?? throw new InvalidOperationException(
                    $"Clip {clip.ClipId} stage {stage.StageId} has no "
                        + $"{architectureLabel} text-video frame count.");
            using ParamSnapshot genericFrameScope = IsWan
                ? null
                : ParamSnapshot.Of(
                    g.UserInput,
                    T2IParamTypes.Text2VideoFrames.Type,
                    T2IParamTypes.VideoFPS.Type);
            if (IsWan)
            {
                genInfo.PrepModelAndCond(g);
                if (genInfo.VideoEndFrame is not null)
                {
                    BuildNativeLastFrameConditioning(genInfo, frames);
                }
                else
                {
                    using ParamSnapshot frameScope = ParamSnapshot.Of(
                        g.UserInput,
                        T2IParamTypes.Text2VideoFrames.Type);
                    g.UserInput.Set(T2IParamTypes.Text2VideoFrames, frames);
                    BuildEmptyTextLatent(clip, stage, genInfo, frames);
                }
            }
            else
            {
                g.UserInput.Set(T2IParamTypes.Text2VideoFrames, frames);
                g.UserInput.Set(T2IParamTypes.VideoFPS, plan.FramesPerSecond);
                genInfo.PrepModelAndCond(g);
                BuildEmptyTextLatent(clip, stage, genInfo, frames);
            }
            g.CurrentMedia.FPS = plan.FramesPerSecond;
            if (!IsWan)
            {
                g.CurrentMedia = g.CurrentMedia.AsSamplingLatent(
                    genInfo.Vae,
                    g.CurrentAudioVae);
            }
            string sampled = g.CreateKSampler(
                genInfo.Model.Path,
                genInfo.PosCond,
                genInfo.NegCond,
                g.CurrentMedia.Path,
                core.CfgScale,
                core.Steps,
                startStep: 0,
                endStep: 10000,
                seed: genInfo.Seed,
                returnWithLeftoverNoise: false,
                addNoise: true,
                sigmin: 0.002,
                sigmax: 1000,
                defsampler: "euler",
                defscheduler: "simple",
                explicitSampler: core.Sampler,
                explicitScheduler: core.Scheduler,
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

    private void BuildEmptyTextLatent(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int frames)
    {
        WGNodeData ambientVae = g.CurrentVae;
        try
        {
            g.CurrentVae = genInfo.Vae;
            g.CurrentMedia = g.EmptyImage(
                (int)genInfo.Width,
                (int)genInfo.Height,
                1);
            ValidateTextLatent(
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

    private void ValidateTextLatent(
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
                    + $"valid {width}x{height}, {frames}-frame {architectureLabel} "
                    + "text-video latent.");
        }
    }

    /// <summary>
    /// Cleanup failures are authoritative after successful host construction. While a host
    /// exception is already unwinding, cleanup is best-effort so it cannot replace that failure.
    /// </summary>
    internal static void RunWanPostHostCleanup(
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
        StockHostVideoStagePayload payload = ResolvePayload(stage);
        StageCorePlan core = stage.Core;
        T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null, sectionId: sectionId)
            ?? throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} could not resolve {architectureLabel} "
                    + "video model "
                + $"'{stage.ResolvedModel.ModelName}'.");
        (string positive, string negative) = _prompts.Resolve(clip, stage);
        int width = g.CurrentMedia?.Width ?? _dimensions.Width;
        int height = g.CurrentMedia?.Height ?? _dimensions.Height;
        return new WorkflowGenerator.ImageToVideoGenInfo
        {
            Generator = g,
            VideoModel = videoModel,
            // Every model swap is an ordinary authored stage. Never let the host append its
            // request-global legacy swap pass to one.
            VideoSwapModel = null,
            VideoSwapPercent = 0.5,
            Frames = IsWan
                ? ResolveWanFrames(clip, stage, sectionId)
                : ResolveGenericFrames(clip, stage),
            VideoCFG = core.CfgScale,
            VideoFPS = plan.FramesPerSecond,
            Width = width,
            Height = height,
            Prompt = positive,
            NegativePrompt = negative,
            Steps = core.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.StageId,
            ContextID = sectionId,
            VideoEndFrame = IsWan ? ResolveWanEndFrame(clip, stage) : null,
        };
    }

    private Image ResolveWanEndFrame(ClipPlan clip, StagePlan stage)
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

    private int? ResolveWanFrames(ClipPlan clip, StagePlan stage, int? sectionId)
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

    private int? ResolveWanPassthroughFrames(ClipPlan clip, StagePlan stage) =>
        stage.Input == StageInputKind.PreviousStage
            ? g.CurrentMedia?.Frames
            : ResolveRequestedFrames(clip, stage, sectionId: null);

    private int? ResolveGenericFrames(ClipPlan clip, StagePlan stage)
    {
        if (clip.Frames is int frames && frames > 0)
        {
            return frames;
        }
        if (stage.Input == StageInputKind.RootMedia)
        {
            return g.UserInput.TryGet(
                T2IParamTypes.VideoFrames,
                out int hostFrames)
                    ? hostFrames
                    : null;
        }
        if (stage.Input != StageInputKind.EmptyLatent)
        {
            return g.CurrentMedia?.Frames;
        }
        return g.UserInput.Get(
            T2IParamTypes.Text2VideoFrames,
            DefaultGenericFrames(ResolvePayload(stage).CompatibilityClassId));
    }

    private static int DefaultGenericFrames(string compatibilityClassId)
    {
        if (compatibilityClassId == T2IModelClassSorter.CompatGenmoMochi.ID)
        {
            return 25;
        }
        if (compatibilityClassId == T2IModelClassSorter.CompatCosmos.ID)
        {
            return 121;
        }
        if (compatibilityClassId is
            "lightricks-ltx-video"
            or "lightricks-ltx-video-2")
        {
            return 97;
        }
        return 73;
    }

    private StockHostVideoStagePayload ResolvePayload(StagePlan stage) =>
        stage.RequireStockHostVideoPayload(ArchitectureId, architectureLabel);
}

internal sealed class StockHostVideoGenerationSessionFactory(
    WorkflowGenerator generator,
    ArchitectureId architectureId,
    string architectureLabel) :
    IArchitectureGenerationSessionFactory
{
    private HostVideoRootSources _rootSources;

    public ArchitectureId ArchitectureId => architectureId;

    public IArchitectureBoundaryAssembler BoundaryAssembler => null;

    public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _rootSources = new(
            generator.CurrentMedia?.Duplicate(),
            generator.CurrentVae?.Duplicate());
    }

    public IVideoGenerationSession CreateSession(
        ArchitectureTimelineSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_rootSources is null)
        {
            throw new InvalidOperationException(
                $"The {architectureLabel} runtime was not prepared before session creation.");
        }
        return new StockHostVideoGenerationSession(
            generator,
            context.Plan,
            _rootSources,
            new HostVideoStageEngine(
                generator,
                context.Plan,
                architectureLabel),
            ArchitectureId,
            architectureLabel);
    }

    public void FinalizeTimeline(
        ArchitectureTimelineFinalizationContext context)
    {
    }
}
