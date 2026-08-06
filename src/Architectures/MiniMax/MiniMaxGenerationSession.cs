using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Generated;
using VideoStages.HostVideo;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;
using Image = SwarmUI.Utils.Image;

namespace VideoStages.Architectures.MiniMax;

internal sealed class MiniMaxGenerationSession(
    WorkflowGenerator g,
    VideoExecutionPlan plan,
    HostVideoRootSources rootSources,
    AudioRuntimeSources audioSources,
    TimelineBoundaries boundaries,
    CapturedHostReference baseReference,
    CapturedHostReference refinerReference,
    VideoStageRunner stageRunner,
    HostRootAdoption rootAdoption) : IVideoGenerationSession
{
    internal const string ArchitectureLabel = "MiniMax H3";

    private readonly PlannedStagePromptResolver _prompts = new(g);
    private readonly InitVideoClipInstaller _initVideoClipInstaller = new(g);

    private readonly (int Width, int Height) _dimensions =
        DimensionSnap.Snap(plan.Width, plan.Height);

    private WGNodeData _entryFirstFrame;
    private WGNodeData _endFrame;
    private WGNodeData _reusedAudio;
    private WGNodeData _boundaryCarryAudio;
    private double _boundaryCarryDuration;
    private double _boundaryCarrySourceStart;
    private ReferenceFramingMode _referenceFraming;

    public ArchitectureId ArchitectureId => MiniMaxArchitectureModule.ArchitectureId;

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ClipPlan clip = context.Clip;
        MiniMaxClipPayload payload = clip.RequireMiniMaxPayload();
        _reusedAudio = null;
        PrepareBoundaryAudioCarry(context);
        _referenceFraming = payload.ReferenceFraming;
        _entryFirstFrame = ResolveFrameReference(
            payload.FirstFrameReference,
            "MiniMax H3 first-frame reference");
        _endFrame = ResolveEndFrame(payload.LastFrameReference);
        if (clip.EntryMode == ArchitectureEntryMode.InitVideo)
        {
            InitVideoPlan source = clip.InitVideo
                ?? throw Invariant.Failure(
                    $"MiniMax H3 clip {clip.ClipId} has no init-video plan.");
            ClipPlan sourceInstallPlan = clip with
            {
                InitVideo = source with
                {
                    TargetWidth = _dimensions.Width,
                    TargetHeight = _dimensions.Height,
                },
            };
            // Any other source replaces the track, and the trim branch would then be built but
            // never consumed — nothing downstream prunes TrimAudioDuration.
            g.CurrentMedia = _initVideoClipInstaller.TryInstall(
                sourceInstallPlan,
                includeSourceAudio: clip.Audio.Base.Kind == AudioSourceKind.Native)
                ?? throw Invariant.Failure(
                    $"clip {clip.ClipId} source video could not be installed.");
            PrepareInitVideoAudio(clip);
            g.CurrentVae = null;
        }
        else if (_entryFirstFrame is not null)
        {
            g.CurrentMedia = _entryFirstFrame;
            // The selected model supplies its own VAE during host preparation. Do not attach an
            // unrelated text-root donor VAE to the uploaded image.
            g.CurrentVae = null;
        }
        else if (clip.EntryMode == ArchitectureEntryMode.TextToVideo)
        {
            g.CurrentMedia = null;
            g.CurrentVae = null;
        }
        else
        {
            g.CurrentMedia = rootSources.Media?.Duplicate()
                ?? throw Invariant.Failure(
                    $"clip {clip.ClipId} has no host image to generate from.");
            g.CurrentVae = rootSources.Vae?.Duplicate();
            _entryFirstFrame = g.CurrentMedia;
        }
        if (clip.EntryMode != ArchitectureEntryMode.InitVideo
            && g.CurrentMedia is not null)
        {
            // Incoming audio belongs to whichever architecture owns the shared root, not to H3.
            g.CurrentMedia.AttachedAudio = null;
        }

        return stageRunner.Execute(clip, ResolvePassthroughFrames, ExecuteGeneratingStage);
    }

    public void Dispose() => stageRunner.Dispose();

    private int? ResolvePassthroughFrames(ClipPlan clip, StagePlan stage) =>
        stage.Input is StageInputKind.PreviousStage or StageInputKind.InitVideo
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
            throw Invariant.Failure(
                $"MiniMax H3 clip {clip.ClipId} cannot continue sampling into stage "
                    + $"{continuation.StageId}.");
        }
        (string positive, string negative) = _prompts.Resolve(clip, stage);
        StockHostVideoStagePayload payload = stage.RequireStockHostVideoPayload(
            ArchitectureId,
            ArchitectureLabel);
        MiniMaxClipPayload clipPayload = clip.RequireMiniMaxPayload();
        PrepareReusableAudio(clipPayload, stage);
        StageCorePlan core = stage.Core;
        using (StageModelLoadScope modelScope = new(
            g,
            clip,
            stage,
            sectionId,
            payload.LoraTargetPolicy))
        using (ParamSnapshot ignoredAudioReference = ParamSnapshot.Of(
            g.UserInput,
            T2IParamTypes.PromptAudios.Type))
        {
            // H3 writes its own audio, so core's reference path must not also wire Prompt Audios
            // into ref_audios. Dropped for this stage only; ParamSnapshot restores it on exit.
            g.UserInput.InternalSet.ValuesInput.Remove(
                T2IParamTypes.PromptAudios.Type.ID);
            WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(
                clip,
                stage,
                sectionId,
                positive,
                negative);
            WGNodeData incoming = null;
            WGNodeData firstFrame = _entryFirstFrame;
            int startStep = 0;
            if (stage.Input is StageInputKind.PreviousStage or StageInputKind.InitVideo)
            {
                stageInput.Configure(clip, stage, genInfo, startStep: 0);
                incoming = g.CurrentMedia
                    ?? throw Invariant.Failure(
                        "A MiniMax H3 refine stage has no incoming decoded media.");
                if (incoming.AttachedAudio is null)
                {
                    throw Invariant.Failure(
                        "A MiniMax H3 refine stage's incoming media carries no audio input.");
                }
                firstFrame = null;
                startStep = HostVideoStageSchedulePolicy.StartStep(
                    core.Steps,
                    core.Control);
            }
            SampleNatively(
                clip,
                stage,
                genInfo,
                incoming,
                firstFrame,
                startStep,
                core.Upscale);
            RestoreReusableAudio(clipPayload, stage);
            if (stage.StageId == clip.Stages.Last(candidate => !candidate.IsPassthrough).StageId)
            {
                AttachDecodedAudio();
            }
            stageInput.NormalizeDecodedOutput(clip, stage, genInfo);
        }
        return false;
    }

    private void PrepareReusableAudio(MiniMaxClipPayload payload, StagePlan stage)
    {
        if (!payload.ReuseAudio
            || stage.ClipStageIndex < 2
            || _reusedAudio is not null
            || g.CurrentMedia?.AttachedAudio is not WGNodeData audio
            || audio.DataType != WGNodeData.DT_LATENT_AUDIO
            || audio.Path is not JArray { Count: 2 })
        {
            return;
        }
        _reusedAudio = audio.Duplicate();
    }

    private void RestoreReusableAudio(MiniMaxClipPayload payload, StagePlan stage)
    {
        if (!payload.ReuseAudio
            || stage.ClipStageIndex < 2
            || _reusedAudio is null
            || g.CurrentMedia is null)
        {
            return;
        }
        WGNodeData current = g.CurrentMedia.Duplicate();
        current.AttachedAudio = _reusedAudio.Duplicate();
        g.CurrentMedia = current;
    }

    private void PrepareBoundaryAudioCarry(ArchitectureClipRuntimeContext context)
    {
        _boundaryCarryAudio = null;
        _boundaryCarryDuration = 0;
        _boundaryCarrySourceStart = 0;
        if (context.PreviousClip is null
            || !boundaries.TryGetAudioCarryWindow(
                context.PreviousClip.ClipId,
                out int windowFrames))
        {
            return;
        }
        if (!context.Clip.Stages.Any(stage => !stage.IsPassthrough))
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: Clip {context.Clip.ClipId} has no generating stage for its "
                    + "incoming audio carry; treating the boundary as a cut.");
            boundaries.DegradeToCut(context.PreviousClip.ClipId);
            return;
        }

        DecodedClipArtifact previous = context.PreviousClipOutput;
        using WorkflowBridge bridge = BridgeSync.For(g);
        if (previous?.Audio?.Resolve(bridge) is null
            || previous.FramesPerSecond <= 0
            || windowFrames <= 0
            || windowFrames > previous.Frames)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: Clip {context.PreviousClip.ClipId} cannot carry audio into "
                    + $"Clip {context.Clip.ClipId} because its decoded audio timing is unavailable; "
                    + "treating the boundary as a cut.");
            boundaries.DegradeToCut(context.PreviousClip.ClipId);
            return;
        }

        _boundaryCarryAudio = new WGNodeData(
            previous.Audio.ToPath(),
            g,
            WGNodeData.DT_AUDIO,
            null);
        _boundaryCarryDuration = windowFrames / (double)previous.FramesPerSecond;
        _boundaryCarrySourceStart =
            (previous.Frames - windowFrames) / (double)previous.FramesPerSecond;
    }

    private void SampleNatively(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData incoming,
        WGNodeData firstFrame,
        int startStep,
        StageUpscalePlan stageUpscale)
    {
        bool ambientImageToVideo = g.IsImageToVideo;
        try
        {
            g.IsImageToVideo = true;
            PrepModelAndPrompt(genInfo);
            int frames = genInfo.Frames
                ?? throw Invariant.Failure(
                    "A MiniMax H3 stage has no resolved frame count.");
            g.CurrentMedia = incoming is null
                ? EntryJointLatent(clip, genInfo, frames)
                : JointLatent(incoming, genInfo);
            ApplyLatentInterpolation(stageUpscale);
            AttachKeyframes(genInfo, firstFrame);
            HostRootClaim claim = rootAdoption.ClaimTextRoot(clip, stage);
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
                id: claim.Sampler,
                hadSpecialCond: true,
                explicitSampler: genInfo.DefaultSampler,
                explicitScheduler: genInfo.DefaultScheduler,
                sectionId: genInfo.ContextID);
            g.CurrentMedia = g.CurrentMedia
                .WithPath([sampled, 0])
                .DecodeLatents(genInfo.Vae, false, claim.Decode);
        }
        finally
        {
            g.IsImageToVideo = ambientImageToVideo;
        }
    }

    /// <summary>Prepares the model and prompt without host keyframe attachment.</summary>
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
        int frames,
        JArray framesConnection = null)
    {
        string empty = g.CreateNode("EmptyMiniMaxH3LatentAV", new JObject()
        {
            ["length"] = framesConnection is null
                ? new JValue(frames)
                : framesConnection,
            ["height"] = genInfo.Height,
            ["width"] = genInfo.Width,
        });
        return new WGNodeData([empty, 0], g, WGNodeData.DT_LATENT_AUDIOVIDEO, genInfo.Model.Compat)
        {
            Width = (int)genInfo.Width,
            Height = (int)genInfo.Height,
            Frames = framesConnection is null ? frames : null,
            FPS = genInfo.VideoFPS,
        };
    }

    private void AttachKeyframes(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData firstFrame)
    {
        if (firstFrame is null && _endFrame is null)
        {
            return;
        }
        int targetWidth = g.CurrentMedia?.Width ?? (int)genInfo.Width;
        int targetHeight = g.CurrentMedia?.Height ?? (int)genInfo.Height;
        using WorkflowBridge bridge = BridgeSync.For(g);
        JArray firstFramePath = firstFrame?.Path is JArray firstPath
            ? ReferenceFramingGraph.Frame(
                bridge,
                firstPath,
                targetWidth,
                targetHeight,
                _referenceFraming,
                unwrapExistingFraming: false)
            : null;
        JArray lastFramePath = _endFrame?.Path is JArray lastPath
            ? ReferenceFramingGraph.Frame(
                bridge,
                lastPath,
                targetWidth,
                targetHeight,
                _referenceFraming,
                unwrapExistingFraming: false)
            : null;
        string keyframes = g.CreateNode("SwarmMiniMaxH3AddKeyframes", new JObject()
        {
            ["vae"] = genInfo.Vae.Path,
            ["latent"] = g.CurrentMedia.Path,
            ["conditioning"] = genInfo.PosCond,
            ["first_frame"] = firstFramePath,
            ["last_frame"] = lastFramePath,
        });
        genInfo.PosCond = [keyframes, 0];
    }

    private WGNodeData EntryJointLatent(
        ClipPlan clip,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int frames)
    {
        WGNodeData combinedAudio = CombineClipAudio(
            clip,
            frames,
            nativeAudio: null,
            suppressNative: true,
            out WGNodeData selectedAudio,
            out IReadOnlyList<(double Start, double End)> preserveWindows);
        JArray framesConnection = null;
        if (clip.Audio.Length.Owner == AudioLengthOwner.Audio
            && MiniMaxArchitectureModule.Instance.Descriptor.AudioSourceKinds.Contains(
                clip.Audio.Base.Kind)
            && selectedAudio is not null
            && combinedAudio?.Path is JArray combinedAudioPath)
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            SwarmAudioLengthToFramesNode lengthToFrames = bridge.AddNode(
                new SwarmAudioLengthToFramesNode().With(
                    FrameRate: plan.FramesPerSecond,
                    FrameGrid: MiniMaxArchitectureModule.FrameGrid,
                    FrameGridOrigin: MiniMaxArchitectureModule.FrameGridOrigin,
                    FrameCountOffset: 0));
            lengthToFrames.AudioInput.TryConnectFromPath(bridge, combinedAudioPath);
            framesConnection = WorkflowBridge.ToPath(lengthToFrames.Frames);
            combinedAudio = combinedAudio.WithPath(
                WorkflowBridge.ToPath(lengthToFrames.Audio));
        }
        WGNodeData joint = EmptyJointLatent(genInfo, frames, framesConnection);
        if (combinedAudio is null)
        {
            return joint;
        }

        WGNodeData samplingAudio = selectedAudio is null && preserveWindows.Count > 0
            ? AudioPreserveWindowBuilder.TryBuild(
                g,
                combinedAudio,
                preserveWindows,
                clip.Stages[0].StageId)
                ?? throw Invariant.Failure(
                    $"MiniMax H3 clip {clip.ClipId} could not preserve its timeline audio windows.")
            : combinedAudio;

        WGNodeData videoLatent = joint.AsLatentImage(genInfo.Vae);
        videoLatent.AttachedAudio = samplingAudio;
        return videoLatent.AsSamplingLatent(genInfo.Vae, g.CurrentAudioVae);
    }

    private WGNodeData JointLatent(
        WGNodeData incoming,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WGNodeData videoLatent = incoming.EncodeToLatent(genInfo.Vae);
        videoLatent.AttachedAudio = incoming.AttachedAudio;
        return videoLatent
            .WithMaskedAudio(g.CurrentAudioVae)
            .AsSamplingLatent(genInfo.Vae, g.CurrentAudioVae);
    }

    private void ApplyLatentInterpolation(StageUpscalePlan upscale)
    {
        if (upscale?.Mode != StageUpscaleMode.Latent || g.CurrentMedia is null)
        {
            return;
        }
        WGNodeData video = g.CurrentMedia.AsLatentImage(g.CurrentVae);
        (int width, int height) = StageUpscaleGraph.ResolveTargetDimensions(
            video.Width ?? _dimensions.Width,
            video.Height ?? _dimensions.Height,
            upscale.Factor);
        using WorkflowBridge bridge = BridgeSync.For(g);
        LatentUpscaleByNode node = bridge.AddNode(new LatentUpscaleByNode().With(
            UpscaleMethod: upscale.MethodName,
            ScaleBy: upscale.Factor));
        node.Samples.ConnectFromPath(bridge, video.Path);
        WGNodeData scaled = video.WithPath(node.LATENT, WGNodeData.DT_LATENT_VIDEO);
        scaled.Width = width;
        scaled.Height = height;
        g.CurrentMedia = scaled.AsSamplingLatent(g.CurrentVae, g.CurrentAudioVae);
    }

    private void PrepareInitVideoAudio(ClipPlan clip)
    {
        WGNodeData current = g.CurrentMedia
            ?? throw Invariant.Failure(
                $"MiniMax H3 clip {clip.ClipId} has no installed init video.");
        WGNodeData combinedAudio = CombineClipAudio(
            clip,
            current.Frames ?? ResolveFrames(clip),
            current.AttachedAudio,
            suppressNative: false,
            out _,
            out _);
        WGNodeData withAudio = current.Duplicate();
        withAudio.AttachedAudio = combinedAudio;
        g.CurrentMedia = withAudio;
    }

    private WGNodeData CombineClipAudio(
        ClipPlan clip,
        int frames,
        WGNodeData nativeAudio,
        bool suppressNative,
        out WGNodeData selectedAudio,
        out IReadOnlyList<(double Start, double End)> preserveWindows)
    {
        AudioRuntimeSources sources = nativeAudio is null
            ? audioSources
            : audioSources with { NativeAudio = nativeAudio };
        selectedAudio = PlannedAudioSourceSelector.Select(
            clip.ClipId,
            clip.Audio.Base,
            sources,
            suppressNative);
        double duration = plan.FramesPerSecond > 0
            ? frames / (double)plan.FramesPerSecond
            : 0;
        AudioSegmentCombiner combiner = new(g);
        WGNodeData combinedAudio = combiner.Combine(
            clip.ClipId,
            clip.Audio.Segments,
            selectedAudio,
            duration,
            out IReadOnlyList<(double Start, double End)> segmentWindows);
        combinedAudio = combiner.OverlayOpeningWindow(
            combinedAudio,
            _boundaryCarryAudio,
            _boundaryCarrySourceStart,
            _boundaryCarryDuration,
            duration);
        preserveWindows = _boundaryCarryAudio is null
            ? segmentWindows
            : [(0, _boundaryCarryDuration), .. segmentWindows];
        return combinedAudio;
    }

    /// <summary>Decodes the latent audio required by the clip artifact.</summary>
    private void AttachDecodedAudio()
    {
        if (g.CurrentMedia?.AttachedAudio is not WGNodeData attached
            || attached.DataType != WGNodeData.DT_LATENT_AUDIO)
        {
            return;
        }
        if (g.CurrentAudioVae is null)
        {
            throw Invariant.Failure(
                "MiniMax H3 produced latent audio but no audio VAE was loaded to decode it.");
        }
        g.CurrentMedia.AttachedAudio = attached.DecodeLatents(g.CurrentAudioVae, true);
    }

    private WGNodeData ResolveFrameReference(
        NativeFrameReferencePlan reference,
        string descriptor)
    {
        if (StringUtils.Equals(reference?.Source, "Upload"))
        {
            Image image = NativeFrameReferences.MaterializeUpload(g, reference, descriptor);
            return image is null
                ? null
                : g.LoadImage(image, "${videostagesminimaxreference}", false);
        }
        CapturedHostReference captured = reference?.Source?.Trim() switch
        {
            string source when StringUtils.Equals(source, "Base") => baseReference,
            string source when StringUtils.Equals(source, "Refiner") => refinerReference,
            _ => null,
        };
        if (reference is not null && captured is null)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: {descriptor} source '{reference.Source}' was not captured; "
                    + "ignoring it for this generation.");
            return null;
        }
        // The base capture happens before the host decodes its latent, so this may need a VAE
        // round-trip. Keyframe inputs take images; handing them a latent breaks the graph.
        WGNodeData decoded = captured?.AsImage();
        if (captured is not null && decoded is null)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: {descriptor} source '{reference.Source}' was captured as a latent "
                    + "with no VAE available to decode it; ignoring it for this generation.");
        }
        return decoded;
    }

    /// <summary>Uses the global final frame only when one clip has no authored reference.</summary>
    private WGNodeData ResolveEndFrame(NativeFrameReferencePlan authored)
    {
        if (authored is not null)
        {
            return ResolveFrameReference(authored, "MiniMax H3 final-frame reference");
        }
        Image global = plan.Clips.Count == 1
            ? g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
            : null;
        return global is null
            ? null
            : g.LoadImage(global, "${videostagesminimaxlastframe}", false);
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
            ?? throw Invariant.Failure(
                $"clip {clip.ClipId} could not resolve MiniMax H3 video model "
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
        };
    }
}
