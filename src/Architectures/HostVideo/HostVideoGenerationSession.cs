using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.HostVideo;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;

namespace VideoStages.Architectures.HostVideo;

/// <summary>
/// Runs the conservative baseline through SwarmUI's proven stock video builders. Family-specific
/// enhancements stay out of this session; only decoded stage chaining and ordinary controls live
/// here.
/// </summary>
internal sealed class HostVideoGenerationSession(
    WorkflowGenerator generator,
    VideoExecutionPlan plan,
    HostVideoRootSources rootSources,
    HostVideoStageEngine stageEngine) : IVideoGenerationSession
{
    private readonly PlannedStagePromptResolver _prompts = new(generator);
    private readonly SourcedClipInstaller _sourcedClipInstaller = new(generator);
    private readonly (int Width, int Height) _dimensions =
        DimensionSnap.Snap(plan.Width, plan.Height);

    public ArchitectureId ArchitectureId =>
        HostVideoArchitectureModule.ArchitectureId;

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ClipPlan clip = context.Clip;
        if (clip.IsSourced)
        {
            SourceVideoPlan source = clip.SourceVideo
                ?? throw new InvalidOperationException(
                    $"Sourced generic host-video clip {clip.ClipId} has no source-video plan.");
            ClipPlan sourceInstallPlan = clip with
            {
                SourceVideo = source with
                {
                    TargetWidth = _dimensions.Width,
                    TargetHeight = _dimensions.Height,
                },
            };
            generator.CurrentMedia = _sourcedClipInstaller.TryInstall(
                sourceInstallPlan,
                includeSourceAudio: false)
                ?? throw new SwarmUserErrorException(
                    $"VideoStages: clip {clip.ClipId} source video could not be installed.");
            generator.CurrentVae = null;
        }
        else if (clip.Input == ClipInputKind.EmptyLatent)
        {
            generator.CurrentMedia = null;
            generator.CurrentVae = null;
        }
        else
        {
            generator.CurrentMedia = rootSources.Media?.Duplicate()
                ?? throw new SwarmUserErrorException(
                    $"VideoStages: clip {clip.ClipId} has no host image to generate from.");
            generator.CurrentVae = rootSources.Vae?.Duplicate();
        }
        if (generator.CurrentMedia is not null)
        {
            generator.CurrentMedia.AttachedAudio = null;
        }

        return stageEngine.Execute(
            clip,
            stage => stage.RequireHostVideoPayload(),
            ResolveFrames,
            ExecuteGeneratingStage);
    }

    public void Dispose() => stageEngine.Dispose();

    private void ExecuteGeneratingStage(
        ClipPlan clip,
        StagePlan stage,
        HostVideoDecodedStageInput stageInput,
        int sectionId)
    {
        StockHostVideoStagePayload payload = stage.RequireHostVideoPayload();
        using (ParamSnapshot promptLoraScope = PromptParser.ApplyLoraScope(
            generator.UserInput,
            clip.ClipId,
            sectionId,
            payload.LoraTargetPolicy))
        using (ParamSnapshot loraScope =
            LoraParams.ApplyNormalLoras(generator.UserInput, payload.Loras))
        using (ParamSnapshot ignoredAudioReference = ParamSnapshot.Of(
            generator.UserInput,
            T2IParamTypes.VideoAudioReference.Type))
        {
            // LTX v2's stock branch reads this request-global enhancement directly. The generic
            // profile does not advertise it, so keep it out of this authored stage while retaining
            // the original request value for metadata.
            generator.UserInput.InternalSet.ValuesInput.Remove(
                T2IParamTypes.VideoAudioReference.Type.ID);

            string stageLoaderKey = $"modelloader_{payload.Model}_image2video";
            bool transientStageLoader =
                promptLoraScope is not null || !payload.Loras.IsDefaultOrEmpty;
            VideoGraphHelpers.RemoveCached(generator, stageLoaderKey);
            try
            {
                WorkflowGenerator.ImageToVideoGenInfo genInfo =
                    BuildGenInfo(clip, stage, sectionId);
                if (stage.Input == StageInputKind.EmptyLatent)
                {
                    ExecuteTextStage(clip, stage, genInfo);
                    stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
                    return;
                }

                int startStep = stage.Input == StageInputKind.RootMedia
                    ? 0
                    : HostVideoStageSchedulePolicy.StartStep(
                        payload.Steps,
                        payload.Control);
                stageInput.Configure(clip, stage, genInfo, startStep);
                WGNodeData ambientAudioVae = generator.CurrentAudioVae;
                try
                {
                    generator.CurrentAudioVae = null;
                    generator.CreateImageToVideo(genInfo);
                }
                finally
                {
                    generator.CurrentAudioVae = ambientAudioVae;
                }
                stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
            }
            finally
            {
                if (transientStageLoader)
                {
                    VideoGraphHelpers.RemoveCached(generator, stageLoaderKey);
                }
            }
        }
    }

    private void ExecuteTextStage(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        StockHostVideoStagePayload payload = stage.RequireHostVideoPayload();
        if (clip.EntryMode != ArchitectureEntryMode.TextToVideo
            || clip.Input != ClipInputKind.EmptyLatent
            || stage.ClipStageIndex != 0)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} stage {stage.StageId} has an invalid generic text-video "
                    + "execution contract.");
        }

        WGNodeData ambientAudioVae = generator.CurrentAudioVae;
        bool ambientImageToVideo = generator.IsImageToVideo;
        try
        {
            generator.IsImageToVideo = true;
            int frames = genInfo.Frames
                ?? throw new InvalidOperationException(
                    $"Clip {clip.ClipId} stage {stage.StageId} has no text-video frame count.");
            using (ParamSnapshot frameScope = ParamSnapshot.Of(
                generator.UserInput,
                T2IParamTypes.Text2VideoFrames.Type,
                T2IParamTypes.VideoFPS.Type))
            {
                generator.UserInput.Set(T2IParamTypes.Text2VideoFrames, frames);
                generator.UserInput.Set(T2IParamTypes.VideoFPS, plan.FramesPerSecond);
                genInfo.PrepModelAndCond(generator);
                WGNodeData ambientVae = generator.CurrentVae;
                try
                {
                    generator.CurrentVae = genInfo.Vae;
                    generator.CurrentMedia = generator.EmptyImage(
                        (int)genInfo.Width,
                        (int)genInfo.Height,
                        1);
                    ValidateTextLatent(clip, stage, generator.CurrentMedia, genInfo, frames);
                }
                finally
                {
                    generator.CurrentVae = ambientVae;
                }

                generator.CurrentMedia.FPS = plan.FramesPerSecond;
                generator.CurrentMedia = generator.CurrentMedia.AsSamplingLatent(
                    genInfo.Vae,
                    generator.CurrentAudioVae);
                string sampled = generator.CreateKSampler(
                    genInfo.Model.Path,
                    genInfo.PosCond,
                    genInfo.NegCond,
                    generator.CurrentMedia.Path,
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
                generator.CurrentMedia = generator.CurrentMedia
                    .WithPath([sampled, 0])
                    .AsRawImage(genInfo.Vae);
            }
        }
        finally
        {
            generator.CurrentAudioVae = ambientAudioVae;
            generator.IsImageToVideo = ambientImageToVideo;
        }
    }

    private void ValidateTextLatent(
        ClipPlan clip,
        StagePlan stage,
        WGNodeData latent,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int frames)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        bool valid = latent is not null
            && latent.DataType == WGNodeData.DT_LATENT_VIDEO
            && latent.Path is Newtonsoft.Json.Linq.JArray { Count: 2 } path
            && bridge.ResolvePath(path) is not null
            && latent.Frames == frames
            && latent.Width == (int)genInfo.Width
            && latent.Height == (int)genInfo.Height;
        if (!valid)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} stage {stage.StageId} could not create a "
                    + $"valid {(int)genInfo.Width}x{(int)genInfo.Height}, {frames}-frame "
                    + "generic text-video latent.");
        }
    }

    private WorkflowGenerator.ImageToVideoGenInfo BuildGenInfo(
        ClipPlan clip,
        StagePlan stage,
        int sectionId)
    {
        StockHostVideoStagePayload payload = stage.RequireHostVideoPayload();
        T2IModel videoModel = generator.UserInput.Get(
            T2IParamTypes.VideoModel,
            null,
            sectionId: sectionId)
            ?? throw new SwarmUserErrorException(
                $"VideoStages: clip {clip.ClipId} could not resolve generic video model "
                    + $"'{payload.Model}'.");
        (string positive, string negative) = _prompts.Resolve(clip, stage);
        return new()
        {
            Generator = generator,
            VideoModel = videoModel,
            VideoSwapModel = null,
            VideoSwapPercent = 0.5,
            Frames = ResolveFrames(clip, stage),
            VideoCFG = payload.CfgScale,
            VideoFPS = plan.FramesPerSecond,
            Width = generator.CurrentMedia?.Width ?? _dimensions.Width,
            Height = generator.CurrentMedia?.Height ?? _dimensions.Height,
            Prompt = positive,
            NegativePrompt = negative,
            Steps = payload.Steps,
            Seed = generator.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.StageId,
            ContextID = sectionId,
            VideoEndFrame = null,
        };
    }

    private int? ResolveFrames(ClipPlan clip, StagePlan stage)
    {
        if (clip.Frames is int frames && frames > 0)
        {
            return frames;
        }
        if (stage.Input == StageInputKind.RootMedia)
        {
            return generator.UserInput.TryGet(
                T2IParamTypes.VideoFrames,
                out int hostFrames)
                    ? hostFrames
                    : null;
        }
        if (stage.Input != StageInputKind.EmptyLatent)
        {
            return generator.CurrentMedia?.Frames;
        }
        return generator.UserInput.Get(
            T2IParamTypes.Text2VideoFrames,
            DefaultFrames(stage.RequireHostVideoPayload().CompatibilityClassId));
    }

    private static int DefaultFrames(string compatibilityClassId)
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

}
