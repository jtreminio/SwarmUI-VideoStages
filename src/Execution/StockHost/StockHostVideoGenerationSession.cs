using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Authoring;
using VideoStages.Execution.Graph;
using VideoStages.Execution.Parameters;
using VideoStages.Planning;

namespace VideoStages.Execution.StockHost;

internal sealed class StockHostVideoGenerationSession(
    WorkflowGenerator g,
    VideoExecutionPlan plan,
    HostVideoRootSources rootSources,
    VideoStageRunner stageRunner,
    HostVideoDecodedStageInput stageInput,
    HostRootAdoption rootAdoption,
    VideoArchitectureDescriptor architecture,
    WanStockHostVideoBehavior wanBehavior = null) : IVideoGenerationSession
{
    private readonly string _architectureLabel = architecture.DisplayName;

    private readonly PlannedStagePromptResolver _prompts = new(g);
    private readonly WanStockHostVideoBehavior _wanBehavior = wanBehavior;

    private readonly (int Width, int Height) _dimensions =
        DimensionSnap.Snap(plan.Width, plan.Height);

    private readonly ClipEntryMedia _entryMedia = new(
        g,
        rootSources,
        architecture.DisplayName);

    public ArchitectureId ArchitectureId => architecture.Id;

    internal static StockHostVideoGenerationSession Create(
        WorkflowGenerator generator,
        ArchitectureTimelineSessionContext context,
        VideoArchitectureDescriptor architecture,
        WanStockHostVideoBehavior wanBehavior = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        HostVideoRootSources rootSources = new(
            generator.CurrentMedia?.Duplicate(),
            generator.CurrentVae?.Duplicate());
        HostVideoDecodedStageInput stageInput = new(
            generator,
            context.Plan.FramesPerSecond,
            architecture.DisplayName,
            preserveAttachedAudio: false);
        return new(
            generator,
            context.Plan,
            rootSources,
            new VideoStageRunner(generator, context.Plan),
            stageInput,
            context.RootAdoption,
            architecture,
            wanBehavior);
    }

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ClipPlan clip = context.Clip;

        if (clip.EntryMode == ArchitectureEntryMode.InitVideo)
        {
            g.CurrentMedia = _entryMedia.InstallInitVideo(
                context,
                _dimensions,
                includeSourceAudio: false);
            g.CurrentVae = null;
        }
        else
        {
            _entryMedia.SelectGenerated(clip, _wanBehavior?.ResolveFirstFrame(clip));
        }
        // Stock-host stages are video-only, but a shared root may carry another architecture's audio.
        if (g.CurrentMedia is not null)
        {
            g.CurrentMedia.AttachedAudio = null;
        }

        return stageRunner.Execute(
            clip,
            ExecutePassthroughStage,
            ExecuteGeneratingStage);
    }

    public void Dispose() => stageRunner.Dispose();

    private void ExecutePassthroughStage(ClipPlan clip, StagePlan stage) =>
        stageInput.ConfigurePassthrough(
            clip,
            stage,
            _wanBehavior is null
                ? ResolveGenericFrames(clip, stage)
                : _wanBehavior.ResolvePassthroughFrames(clip, stage));

    private bool ExecuteGeneratingStage(
        ClipPlan clip,
        StagePlan stage,
        StagePlan continuation,
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
            payload.LoraTarget))
        using (StageModelLoadScope continuationModelScope = continuation is null
            ? null
            : new(
                g,
                clip,
                continuation,
                continuationStageSectionId,
                continuationPayload.LoraTarget,
                T2IParamInput.SectionID_VideoSwap))
        {
            return RunGeneratingStage(
                clip,
                stage,
                continuation,
                sectionId,
                positive,
                negative);
        }
    }

    private bool RunGeneratingStage(
        ClipPlan clip,
        StagePlan stage,
        StagePlan continuation,
        int sectionId,
        string positive,
        string negative)
    {
        // The strip is what makes preflight's host-video.audio-reference.ignored promise true: the
        // fallback accepts any model core recognizes, LTX-2 and H3 included, and core feeds those
        // two Prompt Audios. A WAN stage can only resolve a WAN-compat model, which core never does.
        using ParamSnapshot ignoredAudioReference = _wanBehavior is null
            ? ParamSnapshot.Of(g.UserInput, T2IParamTypes.PromptAudios.Type)
            : null;
        if (_wanBehavior is null)
        {
            g.UserInput.InternalSet.ValuesInput.Remove(
                T2IParamTypes.PromptAudios.Type.ID);
        }
        WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(
            clip,
            stage,
            continuation,
            sectionId,
            positive,
            negative,
            _wanBehavior is null
                ? ResolveGenericFrames(clip, stage)
                : _wanBehavior.ResolveGeneratedFrames(clip, stage, sectionId),
            _wanBehavior?.ResolveEndFrame(clip, continuation ?? stage));
        // Only WAN authors a first frame onto a text entry. That frame goes through the host image
        // builder instead of the native text latent.
        bool materializedFirstFrame =
            _wanBehavior is not null
            && stage.Input == StageInputKind.EmptyLatent
            && g.CurrentMedia is not null;
        if (stage.Input == StageInputKind.EmptyLatent
            && !materializedFirstFrame)
        {
            if (_wanBehavior is null)
            {
                ExecuteGenericTextStage(clip, stage, genInfo);
            }
            else
            {
                ExecuteWanTextStage(clip, stage, genInfo);
            }
            stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
            return false;
        }
        if (!materializedFirstFrame)
        {
            stageInput.Configure(
                clip,
                stage,
                genInfo,
                StageStartStepPolicy.StartStep(stage.Core.Steps, stage.Core.Control));
        }
        ISet<string> preHostNodeIds =
            _wanBehavior?.CapturePreHostNodeIds(stage, genInfo);
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
            _wanBehavior?.RunPostHostCleanup(
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
        string firstVideo = PromptText.SelectVideoOrGlobalPrompt(firstRegion);
        string secondVideo = PromptText.SelectVideoOrGlobalPrompt(secondRegion);
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
            throw Invariant.Failure(
                $"stage {stage.StageId} could not publish its "
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
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WGNodeData ambientAudioVae = g.CurrentAudioVae;
        bool ambientImageToVideo = g.IsImageToVideo;
        try
        {
            g.CurrentAudioVae = null;
            g.IsImageToVideo = true;
            int frames = RequireTextFrames(clip, stage, genInfo);
            genInfo.PrepModelAndCond(g);
            if (genInfo.VideoEndImage is not null)
            {
                _wanBehavior.BuildNativeLastFrameConditioning(
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
        ?? throw Invariant.Failure(
            $"Clip {clip.ClipId} stage {stage.StageId} has no "
                + $"{_architectureLabel} text-video frame count.");

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
            throw Invariant.Failure(
                $"clip {clip.ClipId} stage {stage.StageId} could not create a "
                    + $"valid {width}x{height}, {frames}-frame {_architectureLabel} "
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
        Image videoEndImage)
    {
        StageCorePlan core = stage.Core;
        T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null, sectionId: sectionId)
            ?? throw Invariant.Failure(
                $"clip {clip.ClipId} could not resolve the {_architectureLabel} "
                    + $"model '{stage.ResolvedModel.ModelName}'.");
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
                throw Invariant.Failure(
                    $"clip {clip.ClipId} could not resolve the {_architectureLabel} "
                        + $"model '{continuation.ResolvedModel.ModelName}'.");
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
                : 1d - (double)StageStartStepPolicy.StartStep(
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
            VideoEndImage = videoEndImage,
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
        stage.RequireStockHostVideoPayload(ArchitectureId, _architectureLabel);
}
