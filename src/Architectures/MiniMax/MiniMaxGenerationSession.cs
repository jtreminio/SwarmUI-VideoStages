using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.HostVideo;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;
using Image = SwarmUI.Utils.Image;

namespace VideoStages.Architectures.MiniMax;

/// <summary>
/// Runs one MiniMax H3 clip. H3 samples a joint audio+video latent and keyframes it through
/// conditioning, so image entry delegates to SwarmUI's stock image-to-video builder while text
/// entry and refines build that latent here. Either way the result decodes to video plus audio.
/// </summary>
internal sealed class MiniMaxGenerationSession(
    WorkflowGenerator g,
    VideoExecutionPlan plan,
    HostVideoRootSources rootSources,
    HostVideoStageEngine stageEngine) : IVideoGenerationSession
{
    internal const string ArchitectureLabel = "MiniMax H3";

    private readonly PlannedStagePromptResolver _prompts = new(g);

    private readonly (int Width, int Height) _dimensions =
        DimensionSnap.Snap(plan.Width, plan.Height);

    public ArchitectureId ArchitectureId => MiniMaxArchitectureModule.ArchitectureId;

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ClipPlan clip = context.Clip;
        if (clip.HasInitVideo)
        {
            // The descriptor never publishes the init-video entry mode, so planning rejects it.
            throw VideoStagesInvariant.Failure(
                $"MiniMax H3 clip {clip.ClipId} cannot enter from an init video.");
        }

        WGNodeData authoredFirst = ResolveAuthoredFirstFrame(clip);
        if (authoredFirst is not null)
        {
            g.CurrentMedia = authoredFirst;
            // The selected model supplies its own VAE during host preparation. Do not attach an
            // unrelated text-root donor VAE to the uploaded image.
            g.CurrentVae = null;
        }
        else if (clip.Input == ClipInputKind.EmptyLatent)
        {
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
        if (g.CurrentMedia is not null)
        {
            // Incoming audio belongs to whichever architecture owns the shared root, not to H3.
            g.CurrentMedia.AttachedAudio = null;
        }

        return stageEngine.Execute(clip, ResolvePassthroughFrames, ExecuteGeneratingStage);
    }

    public void Dispose() => stageEngine.Dispose();

    private int? ResolvePassthroughFrames(ClipPlan clip, StagePlan stage) =>
        stage.Input == StageInputKind.PreviousStage
            ? g.CurrentMedia?.Frames
            : ResolveFrames(clip);

    private int ResolveFrames(ClipPlan clip) =>
        StaticGeneratedFrameGrid.SnapUp(
            clip.Frames is int frames && frames > 0
                ? frames
                : g.UserInput.Get(T2IParamTypes.Text2VideoFrames, 124),
            MiniMaxArchitectureModule.FrameGrid,
            MiniMaxArchitectureModule.FrameGridOrigin);

    private bool ExecuteGeneratingStage(
        ClipPlan clip,
        StagePlan stage,
        StagePlan continuation,
        HostVideoDecodedStageInput stageInput,
        int sectionId)
    {
        if (continuation is not null)
        {
            throw VideoStagesInvariant.Failure(
                $"MiniMax H3 clip {clip.ClipId} cannot continue sampling into stage "
                    + $"{continuation.StageId}.");
        }
        (string positive, string negative) = _prompts.Resolve(clip, stage);
        StockHostVideoStagePayload payload = stage.RequireStockHostVideoPayload(
            ArchitectureId,
            ArchitectureLabel);
        StageCorePlan core = stage.Core;
        using (ParamSnapshot promptLoraScope = PromptParser.ApplyLoraScope(
            g.UserInput,
            clip.ClipId,
            sectionId,
            payload.LoraTargetPolicy))
        using (ParamSnapshot loraScope =
            LoraParams.ApplyNormalLoras(g.UserInput, core.Loras))
        using (ParamSnapshot ignoredAudioReference = ParamSnapshot.Of(
            g.UserInput,
            T2IParamTypes.VideoAudioReference.Type))
        {
            // H3 writes its own audio and has no reference-audio input; keep the request-global
            // enhancement in metadata only.
            g.UserInput.InternalSet.ValuesInput.Remove(
                T2IParamTypes.VideoAudioReference.Type.ID);
            string stageLoaderKey =
                $"modelloader_{stage.ResolvedModel.ModelName}_image2video";
            bool transientStageLoader =
                promptLoraScope is not null || !core.Loras.IsDefaultOrEmpty;
            // The host cache key does not encode the active LoRA parameter scope.
            VideoGraphHelpers.RemoveCached(g, stageLoaderKey);
            try
            {
                WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(
                    clip,
                    stage,
                    sectionId,
                    positive,
                    negative);
                if (stage.Input is StageInputKind.PreviousStage or StageInputKind.InitVideo)
                {
                    stageInput.Configure(clip, stage, genInfo, startStep: 0);
                    ExecuteRefineStage(
                        genInfo,
                        HostVideoStageSchedulePolicy.StartStep(core.Steps, core.Control));
                }
                else if (g.CurrentMedia is null)
                {
                    ExecuteEntryStage(genInfo);
                }
                else
                {
                    g.CreateImageToVideo(genInfo);
                }
                AttachDecodedAudio();
                RestoreLiteralDimensions();
                stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
            }
            finally
            {
                if (transientStageLoader)
                {
                    VideoGraphHelpers.RemoveCached(g, stageLoaderKey);
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The stock H3 builder allocates its joint latent fresh, so the sampled media carries no
    /// literal dimensions; the clip artifact contract requires them.
    /// </summary>
    private void RestoreLiteralDimensions()
    {
        if (g.CurrentMedia is not WGNodeData media)
        {
            return;
        }
        media.Width ??= _dimensions.Width;
        media.Height ??= _dimensions.Height;
    }

    private void ExecuteEntryStage(WorkflowGenerator.ImageToVideoGenInfo genInfo) =>
        SampleNatively(genInfo, incoming: null, startStep: 0);

    /// <summary>
    /// Refines decoded media. The joint latent must be rebuilt here rather than through the host
    /// builder: <c>WGNodeData.AsSamplingLatent</c> encodes raw video video-only, and its
    /// audio-concat branch fires only once the video is already a latent.
    /// </summary>
    private void ExecuteRefineStage(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int startStep)
    {
        WGNodeData incoming = g.CurrentMedia
            ?? throw VideoStagesInvariant.Failure(
                "A MiniMax H3 refine stage has no incoming decoded media.");
        if (incoming.AttachedAudio is null)
        {
            throw VideoStagesInvariant.Failure(
                "A MiniMax H3 refine stage's incoming media carries no audio to re-encode.");
        }
        SampleNatively(genInfo, incoming, startStep);
    }

    private void SampleNatively(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData incoming,
        int startStep)
    {
        bool ambientImageToVideo = g.IsImageToVideo;
        try
        {
            g.IsImageToVideo = true;
            PrepModelAndPrompt(genInfo);
            int frames = genInfo.Frames
                ?? throw VideoStagesInvariant.Failure(
                    "A MiniMax H3 stage has no resolved frame count.");
            g.CurrentMedia = incoming is null
                ? EmptyJointLatent(genInfo, frames)
                : JointLatent(incoming, genInfo);
            AttachEndFrameKeyframe(genInfo);
            string sampled = g.CreateKSampler(
                genInfo.Model.Path,
                genInfo.PosCond,
                genInfo.NegCond,
                g.CurrentMedia.Path,
                genInfo.VideoCFG.Value,
                genInfo.Steps,
                startStep,
                endStep: 10000,
                seed: genInfo.Seed,
                returnWithLeftoverNoise: false,
                addNoise: true,
                sigmin: 0.002,
                sigmax: 1000,
                previews: g.UserInput.Get(ComfyUIBackendExtension.VideoPreviewType, "animate"),
                hadSpecialCond: true,
                explicitSampler: genInfo.DefaultSampler,
                explicitScheduler: genInfo.DefaultScheduler,
                sectionId: genInfo.ContextID);
            g.CurrentMedia = g.CurrentMedia
                .WithPath([sampled, 0])
                .AsRawImage(genInfo.Vae);
        }
        finally
        {
            g.IsImageToVideo = ambientImageToVideo;
        }
    }

    /// <summary>
    /// Mirrors <c>ImageToVideoGenInfo.PrepModelAndCond</c> minus its H3 keyframe attach, which
    /// reads the host's current media: absent on text entry, and a whole decoded video on refine.
    /// Keyframes are attached to the sampling latent instead.
    /// </summary>
    private void PrepModelAndPrompt(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        g.FinalLoadedModel = genInfo.VideoModel;
        (genInfo.VideoModel, genInfo.Model, WGNodeData clip, genInfo.Vae) = g.CreateModelLoader(
            genInfo.VideoModel,
            "image2video",
            null,
            true,
            sectionId: genInfo.ContextID);
        genInfo.Clip = clip;
        // H3's reference conditioning reads the host VAE, which this session leaves unset so no
        // foreign root VAE binds to an uploaded frame. The model just loaded its own.
        g.CurrentVae = genInfo.Vae;
        genInfo.PosCond = g.CreateConditioning(
            genInfo.Prompt, clip.Path, genInfo.VideoModel, true, isVideo: true);
        genInfo.NegCond = g.CreateConditioning(
            genInfo.NegativePrompt, clip.Path, genInfo.VideoModel, false, isVideo: true);
    }

    private WGNodeData EmptyJointLatent(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int frames)
    {
        string empty = g.CreateNode("EmptyMiniMaxH3LatentAV", new JObject()
        {
            ["length"] = frames,
            ["height"] = genInfo.Height,
            ["width"] = genInfo.Width,
        });
        return new WGNodeData([empty, 0], g, WGNodeData.DT_LATENT_AUDIOVIDEO, genInfo.Model.Compat)
        {
            Width = (int)genInfo.Width,
            Height = (int)genInfo.Height,
            Frames = frames,
            FPS = genInfo.VideoFPS,
        };
    }

    /// <summary>
    /// H3 conditions on keyframes rather than on an init latent. Only the final frame applies here:
    /// a first frame would have taken the stock image-entry builder.
    /// </summary>
    private void AttachEndFrameKeyframe(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (genInfo.VideoEndFrame is null)
        {
            return;
        }
        string keyframes = g.CreateNode("SwarmMiniMaxH3AddKeyframes", new JObject()
        {
            ["vae"] = genInfo.Vae.Path,
            ["latent"] = g.CurrentMedia.Path,
            ["conditioning"] = genInfo.PosCond,
            ["last_frame"] = g.LoadImage(
                genInfo.VideoEndFrame,
                "${videostagesminimaxlastframe}",
                false).Path,
        });
        genInfo.PosCond = [keyframes, 0];
    }

    private WGNodeData JointLatent(
        WGNodeData incoming,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WGNodeData videoLatent = incoming.EncodeToLatent(genInfo.Vae);
        videoLatent.AttachedAudio = incoming.AttachedAudio;
        return videoLatent.AsSamplingLatent(genInfo.Vae, g.CurrentAudioVae);
    }

    /// <summary>
    /// The separated audio half arrives still latent; the clip artifact contract needs decoded
    /// audio, not a latent the assembler cannot mix.
    /// </summary>
    private void AttachDecodedAudio()
    {
        if (g.CurrentMedia?.AttachedAudio is not WGNodeData attached
            || attached.DataType != WGNodeData.DT_LATENT_AUDIO)
        {
            return;
        }
        if (g.CurrentAudioVae is null)
        {
            throw VideoStagesInvariant.Failure(
                "MiniMax H3 produced latent audio but no audio VAE was loaded to decode it.");
        }
        g.CurrentMedia.AttachedAudio = attached.DecodeLatents(g.CurrentAudioVae, true);
    }

    private WGNodeData ResolveAuthoredFirstFrame(ClipPlan clip)
    {
        Image image = NativeFrameReferences.MaterializeUpload(
            g,
            clip.RequireMiniMaxPayload().FirstFrameReference,
            "MiniMax H3 first-frame reference");
        return image is null
            ? null
            : g.LoadImage(image, "${videostagesminimaxfirstframe}", false);
    }

    /// <summary>
    /// An authored final-frame reference is clip-local. SwarmUI's request-global 'Video End Frame'
    /// has no unambiguous target once a timeline has more than one clip, so it only applies to a
    /// lone MiniMax clip that did not author its own.
    /// </summary>
    private Image ResolveEndFrame(ClipPlan clip)
    {
        Image authored = NativeFrameReferences.MaterializeUpload(
            g,
            clip.RequireMiniMaxPayload().LastFrameReference,
            "MiniMax H3 final-frame reference");
        return authored is not null || plan.Clips.Count != 1
            ? authored
            : g.UserInput.Get(T2IParamTypes.VideoEndFrame, null);
    }

    private WorkflowGenerator.ImageToVideoGenInfo BuildGenInfo(
        ClipPlan clip,
        StagePlan stage,
        int sectionId,
        string positive,
        string negative)
    {
        T2IModel videoModel = g.UserInput.Get(
                T2IParamTypes.VideoModel,
                null,
                sectionId: sectionId)
            ?? throw VideoStagesInvariant.Failure(
                $"VideoStages: clip {clip.ClipId} could not resolve MiniMax H3 video model "
                    + $"'{stage.ResolvedModel.ModelName}'.");
        return new WorkflowGenerator.ImageToVideoGenInfo
        {
            Generator = g,
            VideoModel = videoModel,
            Frames = ResolveFrames(clip),
            VideoCFG = stage.Core.CfgScale,
            VideoFPS = plan.FramesPerSecond,
            Width = _dimensions.Width,
            Height = _dimensions.Height,
            Prompt = positive,
            NegativePrompt = negative,
            Steps = stage.Core.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.StageId,
            ContextID = sectionId,
            VideoEndFrame = ResolveEndFrame(clip),
        };
    }
}

internal sealed class MiniMaxGenerationSessionFactory(WorkflowGenerator generator) :
    IArchitectureGenerationSessionFactory
{
    private HostVideoRootSources _rootSources;

    public ArchitectureId ArchitectureId => MiniMaxArchitectureModule.ArchitectureId;

    public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _rootSources = new(
            generator.CurrentMedia?.Duplicate(),
            generator.CurrentVae?.Duplicate());
    }

    public IVideoGenerationSession CreateSession(ArchitectureTimelineSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_rootSources is null)
        {
            throw VideoStagesInvariant.Failure(
                "The MiniMax H3 runtime was not prepared before session creation.");
        }
        return new MiniMaxGenerationSession(
            generator,
            context.Plan,
            _rootSources,
            new HostVideoStageEngine(
                generator,
                context.Plan,
                MiniMaxGenerationSession.ArchitectureLabel,
                preserveAttachedAudio: true));
    }
}
