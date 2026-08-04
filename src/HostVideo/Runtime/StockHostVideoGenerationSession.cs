using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.HostVideo.Runtime;

internal sealed class StockHostVideoGenerationSession(
    WorkflowGenerator g,
    VideoExecutionPlan plan,
    HostVideoRootSources rootSources,
    VideoStageRunner stageRunner,
    HostRootAdoption rootAdoption,
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

    internal static StockHostVideoGenerationSession Create(
        WorkflowGenerator generator,
        ArchitectureTimelineSessionContext context,
        ArchitectureId architectureId,
        string architectureLabel,
        WanStockHostVideoBehavior wanBehavior = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        HostVideoRootSources rootSources = new(
            generator.CurrentMedia?.Duplicate(),
            generator.CurrentVae?.Duplicate());
        return new(
            generator,
            context.Plan,
            rootSources,
            new VideoStageRunner(
                generator,
                context.Plan,
                architectureLabel),
            context.RootAdoption,
            architectureId,
            architectureLabel,
            wanBehavior);
    }

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ClipPlan clip = context.Clip;

        if (clip.EntryMode == ArchitectureEntryMode.InitVideo)
        {
            InitVideoPlan source = clip.InitVideo
                ?? throw VideoStagesInvariant.Failure(
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
            else if (clip.EntryMode == ArchitectureEntryMode.TextToVideo)
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

        return stageRunner.Execute(
            clip,
            _wanBehavior is not null
                ? _wanBehavior.ResolvePassthroughFrames
                : ResolveGenericFrames,
            ExecuteGeneratingStage);
    }

    public void Dispose() => stageRunner.Dispose();

    private bool ExecuteGeneratingStage(
        ClipPlan clip,
        StagePlan stage,
        StagePlan continuation,
        HostVideoDecodedStageInput stageInput,
        int sectionId)
    {
        (string positive, string negative) = _prompts.Resolve(clip, stage);
        if (continuation is not null)
        {
            (string continuationPositive, string continuationNegative) =
                _prompts.Resolve(clip, continuation);
            if (!TryComposeSamplingContinuationPrompt(
                    positive,
                    continuationPositive,
                    out string combinedPositive)
                || !TryComposeSamplingContinuationPrompt(
                    negative,
                    continuationNegative,
                    out string combinedNegative))
            {
                continuation = null;
            }
            else
            {
                positive = combinedPositive;
                negative = combinedNegative;
            }
        }
        StockHostVideoStagePayload payload = ResolvePayload(stage);
        StockHostVideoStagePayload continuationPayload = continuation is null
            ? null
            : ResolvePayload(continuation);
        int continuationStageSectionId = continuation is null
            ? 0
            : VideoStagesExtension.SectionIdForStage(continuation.StageId);
        using (StageModelLoadScope modelScope = new(
            g,
            clip,
            stage,
            sectionId,
            payload.LoraTargetPolicy))
        using (StageModelLoadScope continuationModelScope = continuation is null
            ? null
            : new(
                g,
                clip,
                continuation,
                continuationStageSectionId,
                continuationPayload.LoraTargetPolicy,
                T2IParamInput.SectionID_VideoSwap))
        {
            return _wanBehavior is null
                ? ExecuteGenericGeneratingStage(
                    clip,
                    stage,
                    continuation,
                    stageInput,
                    sectionId,
                    positive,
                    negative)
                : ExecuteWanGeneratingStage(
                    clip,
                    stage,
                    continuation,
                    stageInput,
                    sectionId,
                    positive,
                    negative);
        }
    }

    private bool ExecuteGenericGeneratingStage(
        ClipPlan clip,
        StagePlan stage,
        StagePlan continuation,
        HostVideoDecodedStageInput stageInput,
        int sectionId,
        string positive,
        string negative)
    {
        using ParamSnapshot ignoredAudioReference = ParamSnapshot.Of(
            g.UserInput,
            T2IParamTypes.PromptAudios.Type);
        g.UserInput.InternalSet.ValuesInput.Remove(
            T2IParamTypes.PromptAudios.Type.ID);
        WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(
            clip,
            stage,
            continuation,
            sectionId,
            positive,
            negative,
            ResolveGenericFrames(clip, stage),
            videoEndFrame: null);
        if (stage.Input == StageInputKind.EmptyLatent)
        {
            ExecuteGenericTextStage(clip, stage, genInfo);
            stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
            return false;
        }
        int startStep = HostVideoStageSchedulePolicy.StartStep(
            stage.Core.Steps,
            stage.Core.Control);
        stageInput.Configure(clip, stage, genInfo, startStep);
        RunHostImageBuilder(stage, continuation, genInfo);
        stageInput.NormalizeDecodedOutput(
            clip,
            continuation ?? stage,
            genInfo);
        return continuation is not null;
    }

    private bool ExecuteWanGeneratingStage(
        ClipPlan clip,
        StagePlan stage,
        StagePlan continuation,
        HostVideoDecodedStageInput stageInput,
        int sectionId,
        string positive,
        string negative)
    {
        WanStockHostVideoBehavior wanBehavior = _wanBehavior;
        WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(
            clip,
            stage,
            continuation,
            sectionId,
            positive,
            negative,
            wanBehavior.ResolveGeneratedFrames(clip, stage, sectionId),
            wanBehavior.ResolveEndFrame(clip, continuation ?? stage));
        bool materializedFirstFrame =
            stage.Input == StageInputKind.EmptyLatent
            && g.CurrentMedia is not null;
        if (stage.Input == StageInputKind.EmptyLatent
            && !materializedFirstFrame)
        {
            ExecuteWanTextStage(clip, stage, genInfo, wanBehavior);
            stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
            return false;
        }
        int startStep = HostVideoStageSchedulePolicy.StartStep(
            stage.Core.Steps,
            stage.Core.Control);
        if (!materializedFirstFrame)
        {
            stageInput.Configure(clip, stage, genInfo, startStep);
        }
        ISet<string> preHostNodeIds =
            wanBehavior.CapturePreHostNodeIds(stage, genInfo);
        Exception hostConstructionError = null;
        try
        {
            RunHostImageBuilder(stage, continuation, genInfo);
        }
        catch (Exception error)
        {
            hostConstructionError = error;
            throw;
        }
        finally
        {
            wanBehavior.RunPostHostCleanup(
                preHostNodeIds,
                hostConstructionError);
        }
        stageInput.NormalizeDecodedOutput(
            clip,
            continuation ?? stage,
            genInfo);
        return continuation is not null;
    }

    private void RunHostImageBuilder(
        StagePlan stage,
        StagePlan continuation,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WGNodeData ambientAudioVae = g.CurrentAudioVae;
        bool ambientImageToVideo = g.IsImageToVideo;
        bool ambientImageToVideoSwap = g.IsImageToVideoSwap;
        try
        {
            g.CurrentAudioVae = null;
            g.CreateImageToVideo(genInfo);
            if (continuation is not null
                && stageRunner.PublishesIntermediateStages)
            {
                PublishSamplingContinuationIntermediate(stage, genInfo);
            }
        }
        finally
        {
            g.CurrentAudioVae = ambientAudioVae;
            g.IsImageToVideo = ambientImageToVideo;
            g.IsImageToVideoSwap = ambientImageToVideoSwap;
        }
    }

    private static bool TryComposeSamplingContinuationPrompt(
        string first,
        string second,
        out string combined)
    {
        PromptRegion firstRegion = new(first ?? "");
        PromptRegion secondRegion = new(second ?? "");
        if (firstRegion.Parts.Count > 0 || secondRegion.Parts.Count > 0)
        {
            combined = null;
            return false;
        }
        string firstVideo = string.IsNullOrWhiteSpace(firstRegion.VideoPrompt)
            ? firstRegion.GlobalPrompt
            : firstRegion.VideoPrompt;
        string secondVideo = string.IsNullOrWhiteSpace(secondRegion.VideoPrompt)
            ? secondRegion.GlobalPrompt
            : secondRegion.VideoPrompt;
        if (string.IsNullOrWhiteSpace(firstVideo)
            != string.IsNullOrWhiteSpace(secondVideo))
        {
            combined = null;
            return false;
        }
        combined = $"<video>{firstVideo}<videoswap>{secondVideo}";
        return true;
    }

    private void PublishSamplingContinuationIntermediate(
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        ComfyNode outputNode = bridge.ResolvePath(g.CurrentMedia?.Path)?.Node as ComfyNode;
        IVaeDecode decode = outputNode as IVaeDecode
            ?? bridge.Graph.FindNearestUpstream<IVaeDecode>(outputNode);
        if (decode?.Samples.Connection?.Node is not ComfyNode lowSampler
            || lowSampler.FindInput("latent_image")?.Connection is not INodeOutput highLatent)
        {
            throw VideoStagesInvariant.Failure(
                $"VideoStages: stage {stage.StageId} could not publish its "
                    + "sampling-continuation intermediate.");
        }
        WGNodeData highMedia = new(
            WorkflowBridge.ToPath(highLatent),
            g,
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Vae.Compat)
        {
            Frames = genInfo.Frames,
            FPS = genInfo.VideoFPS,
            Width = (int?)genInfo.Width,
            Height = (int?)genInfo.Height,
        };
        stageRunner.PublishIntermediate(
            stage,
            highMedia,
            genInfo.Vae);
    }

    private void ExecuteGenericTextStage(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WGNodeData ambientAudioVae = g.CurrentAudioVae;
        bool ambientImageToVideo = g.IsImageToVideo;
        try
        {
            g.IsImageToVideo = true;
            int frames = RequireTextFrames(clip, stage, genInfo);
            using ParamSnapshot frameScope = ParamSnapshot.Of(
                g.UserInput,
                T2IParamTypes.Text2VideoFrames.Type,
                T2IParamTypes.VideoFPS.Type);
            g.UserInput.Set(T2IParamTypes.Text2VideoFrames, frames);
            g.UserInput.Set(T2IParamTypes.VideoFPS, plan.FramesPerSecond);
            genInfo.PrepModelAndCond(g);
            BuildEmptyTextLatent(clip, stage, genInfo, frames);
            g.CurrentMedia.FPS = plan.FramesPerSecond;
            g.CurrentMedia = g.CurrentMedia.AsSamplingLatent(
                genInfo.Vae,
                g.CurrentAudioVae);
            SampleTextStage(clip, stage, genInfo);
        }
        finally
        {
            g.CurrentAudioVae = ambientAudioVae;
            g.IsImageToVideo = ambientImageToVideo;
        }
    }

    private void ExecuteWanTextStage(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WanStockHostVideoBehavior wanBehavior)
    {
        WGNodeData ambientAudioVae = g.CurrentAudioVae;
        bool ambientImageToVideo = g.IsImageToVideo;
        try
        {
            g.CurrentAudioVae = null;
            g.IsImageToVideo = true;
            int frames = RequireTextFrames(clip, stage, genInfo);
            genInfo.PrepModelAndCond(g);
            if (genInfo.VideoEndFrame is not null)
            {
                wanBehavior.BuildNativeLastFrameConditioning(
                    stage,
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
            g.CurrentMedia.FPS = plan.FramesPerSecond;
            SampleTextStage(clip, stage, genInfo);
        }
        finally
        {
            g.CurrentAudioVae = ambientAudioVae;
            g.IsImageToVideo = ambientImageToVideo;
        }
    }

    private int RequireTextFrames(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo) =>
        genInfo.Frames
        ?? throw VideoStagesInvariant.Failure(
            $"Clip {clip.ClipId} stage {stage.StageId} has no "
                + $"{architectureLabel} text-video frame count.");

    private void SampleTextStage(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        StageCorePlan core = stage.Core;
        HostRootClaim claim = rootAdoption.ClaimTextRoot(clip, stage);
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
            id: claim.Sampler,
            explicitSampler: core.Sampler,
            explicitScheduler: core.Scheduler,
            sectionId: genInfo.ContextID);
        g.CurrentMedia = g.CurrentMedia
            .WithPath([sampled, 0])
            .DecodeLatents(genInfo.Vae, false, claim.Decode);
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
        StagePlan continuation,
        int sectionId,
        string positive,
        string negative,
        int? frames,
        Image videoEndFrame)
    {
        StageCorePlan core = stage.Core;
        T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null, sectionId: sectionId)
            ?? throw VideoStagesInvariant.Failure(
                $"VideoStages: clip {clip.ClipId} could not resolve {architectureLabel} "
                    + "video model "
                + $"'{stage.ResolvedModel.ModelName}'.");
        T2IModel continuationModel = null;
        if (continuation is not null)
        {
            continuationModel = g.UserInput.Get(
                T2IParamTypes.VideoModel,
                null,
                sectionId: VideoStagesExtension.SectionIdForStage(
                    continuation.StageId));
            if (continuationModel is null)
            {
                throw VideoStagesInvariant.Failure(
                    $"VideoStages: clip {clip.ClipId} could not resolve {architectureLabel} "
                        + $"video model '{continuation.ResolvedModel.ModelName}'.");
            }
        }
        int width = g.CurrentMedia?.Width ?? _dimensions.Width;
        int height = g.CurrentMedia?.Height ?? _dimensions.Height;
        return new WorkflowGenerator.ImageToVideoGenInfo
        {
            Generator = g,
            VideoModel = videoModel,
            // Only an architecture-planned continuation reaches the host swap pass. The
            // request-global legacy swap remains ignored.
            VideoSwapModel = continuationModel,
            VideoSwapPercent = continuation is null
                ? 0.5
                : 1d - (double)HostVideoStageSchedulePolicy.StartStep(
                    continuation.Core.Steps,
                    continuation.Core.Control) / continuation.Core.Steps,
            Frames = frames,
            VideoCFG = core.CfgScale,
            VideoFPS = plan.FramesPerSecond,
            Width = width,
            Height = height,
            Prompt = positive,
            NegativePrompt = negative,
            Steps = core.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.StageId,
            ContextID = sectionId,
            VideoEndFrame = videoEndFrame,
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
