using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.HostVideo.Runtime;

/// <summary>
/// Runs architectures whose stages delegate to SwarmUI's stock video builders. WAN-specific
/// behavior is supplied by <see cref="WanStockHostVideoBehavior"/>.
/// </summary>
internal sealed class StockHostVideoGenerationSession(
    WorkflowGenerator g,
    VideoExecutionPlan plan,
    HostVideoRootSources rootSources,
    HostVideoStageEngine stageEngine,
    ArchitectureId architectureId,
    string architectureLabel,
    WanStockHostVideoBehavior wanBehavior = null) : IVideoGenerationSession
{
    private readonly PlannedStagePromptResolver _prompts = new(g);
    private readonly InitVideoClipInstaller _initVideoClipInstaller = new(g);
    private readonly WanStockHostVideoBehavior _wanBehavior = wanBehavior;

    /// <summary>The timeline resolution on the shared VideoStages pixel grid.</summary>
    private readonly (int Width, int Height) _dimensions =
        DimensionSnap.Snap(plan.Width, plan.Height);

    public ArchitectureId ArchitectureId => architectureId;

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ClipPlan clip = context.Clip;

        if (clip.HasInitVideo)
        {
            InitVideoPlan source = clip.InitVideo
                ?? throw new InvalidOperationException(
                    $"InitVideo {architectureLabel} clip {clip.ClipId} has no init-video plan.");
            ClipPlan sourceInstallPlan = clip with
            {
                InitVideo = source with
                {
                    TargetWidth = _dimensions.Width,
                    TargetHeight = _dimensions.Height,
                },
            };
            g.CurrentMedia = _initVideoClipInstaller.TryInstall(
                sourceInstallPlan,
                includeSourceAudio: false)
                ?? throw VideoStagesInvariant.Failure(
                    $"VideoStages: clip {clip.ClipId} source video could not be installed.");
            g.CurrentVae = null;
        }
        else
        {
            WGNodeData authoredFirst =
                _wanBehavior?.ResolveFirstFrame(clip);
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
                    ?? throw VideoStagesInvariant.Failure(
                        $"VideoStages: clip {clip.ClipId} has no host image to generate from.");
                g.CurrentVae = rootSources.Vae?.Duplicate();
            }
        }
        // Stock-host stages are video-only, but a shared root may carry another architecture's audio.
        if (g.CurrentMedia is not null)
        {
            g.CurrentMedia.AttachedAudio = null;
        }

        return stageEngine.Execute(
            clip,
            _wanBehavior is not null
                ? _wanBehavior.ResolvePassthroughFrames
                : ResolveGenericFrames,
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
        using (ParamSnapshot ignoredAudioReference = _wanBehavior is not null
            ? null
            : ParamSnapshot.Of(
                g.UserInput,
                T2IParamTypes.VideoAudioReference.Type))
        {
            if (_wanBehavior is null)
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
                    _wanBehavior is not null
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
                ISet<string> preHostNodeIds =
                    _wanBehavior?.CapturePreHostNodeIds(payload, genInfo);
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
                    _wanBehavior?.RunPostHostCleanup(
                        preHostNodeIds,
                        hostConstructionError);
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
            if (_wanBehavior is not null)
            {
                g.CurrentAudioVae = null;
            }
            g.IsImageToVideo = true;
            int frames = genInfo.Frames
                ?? throw new InvalidOperationException(
                    $"Clip {clip.ClipId} stage {stage.StageId} has no "
                        + $"{architectureLabel} text-video frame count.");
            using ParamSnapshot genericFrameScope = _wanBehavior is not null
                ? null
                : ParamSnapshot.Of(
                    g.UserInput,
                    T2IParamTypes.Text2VideoFrames.Type,
                    T2IParamTypes.VideoFPS.Type);
            if (_wanBehavior is not null)
            {
                genInfo.PrepModelAndCond(g);
                if (genInfo.VideoEndFrame is not null)
                {
                    _wanBehavior.BuildNativeLastFrameConditioning(
                        genInfo,
                        frames);
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
            if (_wanBehavior is null)
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
            throw VideoStagesInvariant.Failure(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} could not create a "
                    + $"valid {width}x{height}, {frames}-frame {architectureLabel} "
                    + "text-video latent.");
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
            ?? throw VideoStagesInvariant.Failure(
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
            Frames = _wanBehavior is not null
                ? _wanBehavior.ResolveGeneratedFrames(
                    clip,
                    stage,
                    sectionId)
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
            VideoEndFrame = _wanBehavior?.ResolveEndFrame(clip, stage),
        };
    }

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
    string architectureLabel,
    Func<VideoExecutionPlan, WanStockHostVideoBehavior> wanBehaviorFactory = null) :
    IArchitectureGenerationSessionFactory
{
    private HostVideoRootSources _rootSources;

    public ArchitectureId ArchitectureId => architectureId;

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
            architectureLabel,
            wanBehaviorFactory?.Invoke(context.Plan));
    }
}
