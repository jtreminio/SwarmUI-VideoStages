using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Execution.Audio;
using VideoStages.Execution.Graph;
using VideoStages.Execution.Parameters;
using VideoStages.Execution;
using VideoStages.Generated;
using VideoStages.Execution.StockHost;
using VideoStages.Planning;
using Image = SwarmUI.Utils.Image;

namespace VideoStages.Architectures.MiniMax;

internal sealed class MiniMaxGenerationSession(
    WorkflowGenerator g,
    VideoExecutionPlan plan,
    HostVideoRootSources rootSources,
    AudioRuntimeSources audioSources,
    Timeline.Boundaries boundaries,
    CapturedHostReference baseReference,
    CapturedHostReference refinerReference,
    VideoStageRunner stageRunner,
    HostVideoDecodedStageInput stageInput,
    HostRootAdoption rootAdoption) : IVideoGenerationSession
{
    private readonly PlannedStagePromptResolver _prompts = new(g);
    private readonly MiniMaxBoundaryReferenceBuilder _boundaryReferenceBuilder =
        new(g, plan, boundaries);

    private readonly ClipEntryMedia _entryMedia = new(
        g,
        rootSources,
        MiniMaxArchitectureModule.Instance.Descriptor.DisplayName);

    private readonly (int Width, int Height) _dimensions =
        DimensionSnap.Snap(plan.Width, plan.Height);

    private WGNodeData _firstFrame;
    private WGNodeData _endFrame;
    private WGNodeData _reusedAudio;
    private WGNodeData _boundaryCarryAudio;
    private MiniMaxBoundaryReference _boundaryReference;
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
        _boundaryReference = _boundaryReferenceBuilder.TryBuild(
            context,
            ResolveFrames(context.Clip));
        PrepareBoundaryAudioCarry(context);
        _referenceFraming = payload.ReferenceFraming;
        _firstFrame = ResolveFrameReference(
            payload.FirstFrameReference,
            "MiniMax H3 first keyframe");
        _endFrame = ResolveEndFrame(payload.LastFrameReference);
        bool hasExplicitKeyframe = payload.FirstFrameReference is not null
            || payload.LastFrameReference is not null
            || clip.Stages.Any(stage =>
                stage.ArchitecturePayload is StockHostVideoStagePayload stagePayload
                && stagePayload.FrameReferences.Any(reference =>
                    !reference.IsEndpoint && reference.Strength > 0));
        if (clip.EntryMode == ArchitectureEntryMode.InitVideo)
        {
            // TrimAudioDuration is not swept when another configured source replaces this track.
            g.CurrentMedia = _entryMedia.InstallInitVideo(
                context,
                includeSourceAudio: UsesInitVideoSoundtrack(clip.Audio.Base));
            PrepareInitVideoAudio(clip);
            g.CurrentVae = null;
        }
        else
        {
            _entryMedia.SelectGenerated(clip, _firstFrame);
            // A host image is the opening frame only when the clip has no authored keyframe.
            if (!hasExplicitKeyframe)
            {
                _firstFrame = g.CurrentMedia;
            }
        }
        if (clip.EntryMode != ArchitectureEntryMode.InitVideo
            && g.CurrentMedia is not null)
        {
            // Incoming audio belongs to whichever architecture owns the shared root, not to H3.
            g.CurrentMedia.AttachedAudio = null;
        }

        return stageRunner.Execute(clip, ExecutePassthroughStage, ExecuteGeneratingStage);
    }

    public void Dispose() => stageRunner.Dispose();

    private void ExecutePassthroughStage(ClipPlan clip, StagePlan stage) =>
        stageInput.ConfigurePassthrough(
            clip,
            stage,
            ResolvePassthroughFrames(clip, stage));

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
            MiniMaxArchitectureModule.Instance.Descriptor.DisplayName);
        MiniMaxClipPayload clipPayload = clip.RequireMiniMaxPayload();
        PrepareReusableAudio(clipPayload, stage);
        StageCorePlan core = stage.Core;
        using (StageModelLoadScope modelScope = new(
            g,
            clip,
            stage,
            sectionId,
            payload.LoraTarget))
        using (ParamSnapshot ignoredAudioReference = ParamSnapshot.Of(
            g.UserInput,
            T2IParamTypes.PromptAudios.Type))
        {
            // H3 writes its own audio, so core's reference path must not also wire Prompt Audios
            // into ref_audios.
            g.UserInput.InternalSet.ValuesInput.Remove(
                T2IParamTypes.PromptAudios.Type.ID);
            WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(
                clip,
                stage,
                sectionId,
                positive,
                negative);
            WGNodeData incoming = null;
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
                startStep = StageStartStepPolicy.StartStep(
                    core.Steps,
                    core.Control);
            }
            SampleJointLatent(
                clip,
                stage,
                genInfo,
                incoming,
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
            RequestWarnings.Track(
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
            RequestWarnings.Track(
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

    private void SampleJointLatent(
        ClipPlan clip,
        StagePlan stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData incoming,
        int startStep,
        StageUpscalePlan stageUpscale)
    {
        bool ambientImageToVideo = g.IsImageToVideo;
        try
        {
            g.IsImageToVideo = true;
            MiniMaxClipPayload payload = clip.RequireMiniMaxPayload();
            PrepModelAndPrompt(clip, genInfo, payload.References);
            MiniMaxAttentionWindowGraph.Apply(g, payload.AttentionWindowSeconds, genInfo);
            int frames = genInfo.Frames
                ?? throw Invariant.Failure(
                    "A MiniMax H3 stage has no resolved frame count.");
            HostRootClaim claim = rootAdoption.ClaimTextRoot(clip, stage, includeLatent: true);
            g.CurrentMedia = incoming is null
                ? EntryJointLatent(clip, genInfo, frames, claim.Latent)
                : JointLatent(incoming, genInfo);
            ApplyLatentInterpolation(stageUpscale);
            AttachKeyframes(
                genInfo,
                stage.RequireStockHostVideoPayload(
                        ArchitectureId,
                        MiniMaxArchitectureModule.Instance.Descriptor.DisplayName)
                    .FrameReferences);
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

    private void PrepModelAndPrompt(
        ClipPlan clip,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        IReadOnlyList<MiniMaxReferencePlan> references)
    {
        g.FinalLoadedModel = genInfo.VideoModel;
        (genInfo.VideoModel, genInfo.Model, WGNodeData textEncoder, genInfo.Vae) =
            g.CreateModelLoader(
                genInfo.VideoModel,
                "image2video",
                null,
                true,
                sectionId: genInfo.ContextID);
        textEncoder = MiniMaxTextEncoderGraph.Apply(
            g,
            clip.RequireMiniMaxPayload().TextEncoder,
            textEncoder);
        genInfo.Clip = textEncoder;
        // H3's reference conditioning reads the host VAE, which this session leaves unset so no
        // foreign root VAE binds to an uploaded frame. The model just loaded its own.
        g.CurrentVae = genInfo.Vae;
        // Whole-clip references are tokenized WITH the prompt, so they replace the text encode
        // rather than decorating its output.
        genInfo.PosCond = BuildReferenceConditioning(clip.ClipId, references, genInfo)
            ?? g.CreateConditioning(
                genInfo.Prompt, textEncoder.Path, genInfo.VideoModel, true, isVideo: true);
        genInfo.NegCond = g.CreateConditioning(
            genInfo.NegativePrompt, textEncoder.Path, genInfo.VideoModel, false, isVideo: true);
    }

    private WGNodeData EmptyJointLatent(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int frames,
        string latentNodeId = null,
        JArray framesConnection = null)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        EmptyMiniMaxH3LatentAVNode emptyNode = latentNodeId is null
            ? bridge.AddNode(new EmptyMiniMaxH3LatentAVNode())
            : bridge.AddNode(new EmptyMiniMaxH3LatentAVNode(), latentNodeId);
        emptyNode.With(Width: (int)genInfo.Width, Height: (int)genInfo.Height);
        emptyNode.Length.SetFromToken(bridge, (JToken)framesConnection ?? new JValue(frames));
        string empty = emptyNode.Id;
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
        IReadOnlyList<FrameRefPlan> frameReferences)
    {
        FrameRefPlan[] arbitrary = [.. (frameReferences ?? []).Where(reference =>
            !reference.IsEndpoint && reference.Strength > 0)];
        if (_firstFrame is null && _endFrame is null && arbitrary.Length == 0)
        {
            return;
        }
        List<(FrameRefPlan Reference, WGNodeData Image)> resolved = [];
        foreach (FrameRefPlan reference in arbitrary)
        {
            WGNodeData image = ResolveFrameReference(
                new NativeFrameReferencePlan(
                    reference.RawSource,
                    reference.UploadFileName,
                    reference.InlineData),
                $"MiniMax H3 frame {reference.GuideFrameIndex} keyframe");
            if (image?.Path is JArray)
            {
                resolved.Add((reference, image));
            }
        }
        int targetWidth = g.CurrentMedia?.Width ?? (int)genInfo.Width;
        int targetHeight = g.CurrentMedia?.Height ?? (int)genInfo.Height;
        using WorkflowBridge bridge = BridgeSync.For(g);
        JArray firstFramePath = _firstFrame?.Path is JArray firstPath
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
        if (firstFramePath is not null || lastFramePath is not null)
        {
            SwarmMiniMaxH3AddKeyframesNode keyframes = bridge.AddNode(
                new SwarmMiniMaxH3AddKeyframesNode());
            keyframes.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
            keyframes.Latent.ConnectFromPath(bridge, g.CurrentMedia.Path);
            keyframes.ConditioningInput.ConnectFromPath(bridge, genInfo.PosCond);
            keyframes.FirstFrame.TryConnectFromPath(bridge, firstFramePath);
            keyframes.LastFrame.TryConnectFromPath(bridge, lastFramePath);
            genInfo.PosCond = WorkflowBridge.ToPath(keyframes.Conditioning);
        }
        foreach ((FrameRefPlan reference, WGNodeData image) in resolved)
        {
            JArray imagePath = (JArray)image.Path;
            JArray framed = ReferenceFramingGraph.Frame(
                bridge,
                imagePath,
                targetWidth,
                targetHeight,
                _referenceFraming,
                unwrapExistingFraming: false);
            MiniMaxH3AddGuideNode guide = bridge.AddNode(new MiniMaxH3AddGuideNode().With(
                FrameIdx: reference.GuideFrameIndex));
            guide.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond);
            guide.Latent.ConnectFromPath(bridge, g.CurrentMedia.Path);
            guide.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
            guide.Image.ConnectFromPath(bridge, framed);
            genInfo.PosCond = WorkflowBridge.ToPath(guide.Positive);
        }
    }

    private WGNodeData EntryJointLatent(
        ClipPlan clip,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int frames,
        string latentNodeId)
    {
        WGNodeData combinedAudio = CombineClipAudio(
            clip,
            frames,
            nativeAudio: null,
            suppressNative: true,
            out WGNodeData selectedAudio,
            out IReadOnlyList<(double Start, double End)> preserveWindows);
        JArray framesConnection = null;
        if (clip.Audio.LengthOwner == AudioLengthOwner.Audio
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
        WGNodeData joint = EmptyJointLatent(genInfo, frames, latentNodeId, framesConnection);
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
        if (selectedAudio is null
            && nativeAudio is not null
            && UsesInitVideoSoundtrack(clip.Audio.Base))
        {
            selectedAudio = nativeAudio;
        }
        double duration = plan.FramesPerSecond > 0
            ? frames / (double)plan.FramesPerSecond
            : 0;
        AudioSpanCombiner combiner = new(g);
        WGNodeData combinedAudio = combiner.Combine(
            clip.ClipId,
            clip.Audio.Spans,
            selectedAudio,
            duration,
            out IReadOnlyList<(double Start, double End)> spanWindows);
        combinedAudio = combiner.OverlayOpeningWindow(
            combinedAudio,
            _boundaryCarryAudio,
            _boundaryCarrySourceStart,
            _boundaryCarryDuration,
            duration);
        preserveWindows = _boundaryCarryAudio is null
            ? spanWindows
            : [(0, _boundaryCarryDuration), .. spanWindows];
        return combinedAudio;
    }

    private static bool UsesInitVideoSoundtrack(AudioBaseSourcePlan source) =>
        source.Kind == AudioSourceKind.Native || !source.HasConfiguredTrack;

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
        using ParamSnapshot ignoredAudioTiling = ParamSnapshot.Of(
            g.UserInput,
            T2IParamTypes.VAETileSize.Type);
        g.UserInput.InternalSet.ValuesInput.Remove(T2IParamTypes.VAETileSize.Type.ID);
        g.CurrentMedia.AttachedAudio = attached.DecodeLatents(g.CurrentAudioVae, true);
    }

    private WGNodeData ResolveFrameReference(
        NativeFrameReferencePlan reference,
        string descriptor)
    {
        if (StringUtils.Equals(reference?.Source, MediaSource.Upload))
        {
            Image image = NativeFrameReferences.MaterializeUpload(g, reference, descriptor);
            return image is null
                ? null
                : g.LoadImage(image, "${videostagesminimaxreference}", false);
        }
        return reference is null ? null : ResolveHostCapture(reference.Source, descriptor);
    }

    /// <summary>
    /// The host stage image a "Base", "Refiner" or Base2Edit <c>editN</c> source names, or null with
    /// a warning. Base2Edit publishes its edit stages itself, so those need no capture of our own.
    /// </summary>
    private WGNodeData ResolveHostCapture(string source, string descriptor)
    {
        string value = source?.Trim() ?? "";
        CapturedHostReference captured = null;
        if (StringUtils.Equals(value, MediaSource.Base))
        {
            captured = baseReference;
        }
        else if (StringUtils.Equals(value, MediaSource.Refiner))
        {
            captured = refinerReference;
        }
        else if (MediaSource.TryParseBase2EditIndex(value, out int editStage)
            && Base2EditStageRefs.TryGet(
                g,
                editStage,
                out WGNodeData editMedia,
                out WGNodeData editVae))
        {
            captured = new(editMedia, editVae);
        }
        if (captured is null)
        {
            RequestWarnings.Track(
                g.UserInput,
                $"VideoStages: {descriptor} source '{source}' was not available; "
                    + "ignoring it for this generation.");
            return null;
        }
        // The base capture happens before the host decodes its latent, so this may need a VAE
        // round-trip. Image inputs take images; handing them a latent breaks the graph.
        WGNodeData decoded = captured.AsImage();
        if (decoded is null)
        {
            RequestWarnings.Track(
                g.UserInput,
                $"VideoStages: {descriptor} source '{source}' was captured as a latent "
                    + "with no VAE available to decode it; ignoring it for this generation.");
        }
        return decoded;
    }

    /// <summary>
    /// Builds H3's <c>MiniMaxH3ReferenceToVideo</c> conditioning, or null when the clip authored
    /// no usable reference. Its second output is an empty AV latent this session already builds
    /// itself, so only the conditioning is taken.
    /// </summary>
    private JArray BuildReferenceConditioning(
        int clipId,
        IReadOnlyList<MiniMaxReferencePlan> references,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        List<JArray> images = [];
        List<JArray> videos = _boundaryReference is null
            ? []
            : [_boundaryReference.Video];
        List<JArray> videoAudios = _boundaryReference is null
            ? []
            : [_boundaryReference.Audio];
        List<JArray> audios = [];
        foreach (MiniMaxReferencePlan reference in references)
        {
            string descriptor =
                $"clip {clipId} {MiniMaxClipReferences.Label(reference.Kind)} reference";
            switch (reference.Kind)
            {
                case ClipReferenceKind.Image:
                    if (ReferenceImage(reference, descriptor, images.Count) is JArray image)
                    {
                        images.Add(image);
                    }
                    break;
                case ClipReferenceKind.Video:
                    if (videos.Count >= MiniMaxClipReferences.MaxVideos)
                    {
                        RequestWarnings.Track(
                            g.UserInput,
                            $"VideoStages: {descriptor} exceeds MiniMax H3's "
                                + $"{MiniMaxClipReferences.MaxVideos}-video limit because "
                                + "Continue reserves reference video 0; ignoring it for this generation.");
                        break;
                    }
                    WGNodeData video = ResolveReferenceVideo(reference, descriptor);
                    if (video is null)
                    {
                        break;
                    }
                    videos.Add(ConformReferenceVideo(video, reference, descriptor));
                    videoAudios.Add(
                        reference.IncludeSoundtrack
                            ? TrimReferenceAudio(
                                video.AttachedAudio?.Path as JArray,
                                reference)
                            : null);
                    break;
                default:
                    if (ResolveReferenceAudio(reference, descriptor) is JArray audio)
                    {
                        audios.Add(TrimReferenceAudio(audio, reference));
                    }
                    break;
            }
        }
        if (images.Count + videos.Count + audios.Count == 0)
        {
            return null;
        }

        WGNodeData audioVae = g.CurrentAudioVae
            ?? throw Invariant.Failure(
                "MiniMax H3 reference conditioning requires the model's audio VAE.");
        int frames = genInfo.Frames
            ?? throw Invariant.Failure(
                "MiniMax H3 reference conditioning has no resolved frame count.");
        using WorkflowBridge bridge = BridgeSync.For(g);
        MiniMaxH3ReferenceToVideoNode node = bridge.AddNode(
            new MiniMaxH3ReferenceToVideoNode().With(
                Prompt: genInfo.Prompt,
                Width: (int)genInfo.Width,
                Height: (int)genInfo.Height,
                Length: frames,
                RefImageSize: "match"));
        node.Clip.ConnectFromPath(bridge, genInfo.Clip.Path);
        node.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
        node.AudioVae.ConnectFromPath(bridge, audioVae.Path);
        Fill(node.RefImages, images);
        Fill(node.RefVideos, videos);
        Fill(node.RefVideoAudios, videoAudios);
        Fill(node.RefAudios, audios);
        return WorkflowBridge.ToPath(node.Positive);

        void Fill(INodeInputList list, IReadOnlyList<JArray> paths)
        {
            foreach (JArray path in paths)
            {
                list.AppendUnsetSlot().TryConnectToUntyped(bridge.ResolvePath(path));
            }
        }
    }

    /// <summary>
    /// Puts a reference video on the timebase and size H3 expects.
    /// <para>
    /// The node reads a reference as a plain frame batch at its own fixed 24 fps: it truncates to
    /// the generated frame count, samples every twelfth frame for the text encoder, and stamps
    /// those at half-second intervals. A 30 or 60 fps upload therefore arrives as sped-up motion
    /// cut short of the seconds it was meant to cover, so it is resampled first. Scaling comes
    /// after, on the frames that survived, and only buys back reference tokens — H3 fits every
    /// reference onto its own 32-aligned canvas regardless.
    /// </para>
    /// <para>
    /// An authored trim window lands between the two: the resample fixes the timebase, so the
    /// authored seconds convert to frames at a rate this graph chose rather than one the file
    /// happened to have. A reference whose rate is unknown cannot make that conversion and keeps
    /// its whole length.
    /// </para>
    /// </summary>
    private JArray ConformReferenceVideo(
        WGNodeData video,
        MiniMaxReferencePlan reference,
        string descriptor)
    {
        JArray frames = video.Path;
        JArray fps = video.FPS as JArray;
        double scale = reference.MediaScale;
        bool trims = reference.LengthSeconds > 0;
        if (fps is null && !trims && scale >= 1)
        {
            return frames;
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        if (fps is not null)
        {
            SwarmVideoResampleFPSNode resampled = bridge.AddNode(
                new SwarmVideoResampleFPSNode().With(
                    FpsOut: MiniMaxArchitectureModule.ReferenceFramesPerSecond,
                    Method: "linear"));
            resampled.ImagesInput.ConnectFromPath(bridge, frames);
            resampled.FpsIn.ConnectFromPath(bridge, fps);
            frames = WorkflowBridge.ToPath(resampled.Images);
        }
        else if (trims)
        {
            RequestWarnings.Track(
                g.UserInput,
                $"VideoStages: {descriptor} carries no frame rate, so its trim cannot be "
                    + "converted to frames; the whole reference is used for this generation.");
        }
        if (trims && fps is not null)
        {
            double rate = MiniMaxArchitectureModule.ReferenceFramesPerSecond;
            SwarmFrameWindowNode window = bridge.AddNode(new SwarmFrameWindowNode().With(
                StartFrame: (int)Math.Round(reference.StartSeconds * rate),
                FrameCount: Math.Max(1, (int)Math.Round(reference.LengthSeconds * rate))));
            window.ImagesInput.ConnectFromPath(bridge, frames);
            frames = WorkflowBridge.ToPath(window.Images);
        }
        if (scale >= 1)
        {
            return frames;
        }
        ImageScaleByNode scaled = bridge.AddNode(new ImageScaleByNode().With(
            UpscaleMethod: "lanczos",
            ScaleBy: scale));
        scaled.Image.ConnectFromPath(bridge, frames);
        return WorkflowBridge.ToPath(scaled.IMAGE);
    }

    /// <summary>Applies the authored range to video soundtracks and audio references.</summary>
    private JArray TrimReferenceAudio(JArray path, MiniMaxReferencePlan reference)
    {
        if (path is null || reference.LengthSeconds <= 0)
        {
            return path;
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        TrimAudioDurationNode trim = bridge.AddNode(new TrimAudioDurationNode().With(
            StartIndex: reference.StartSeconds,
            Duration: reference.LengthSeconds));
        trim.Audio.ConnectFromPath(bridge, path);
        return WorkflowBridge.ToPath(trim.AUDIO);
    }

    private JArray ReferenceImage(
        MiniMaxReferencePlan reference,
        string descriptor,
        int index)
    {
        if (MediaSource.TryParseControlNetIndex(reference.Source, out int controlNetIndex))
        {
            ControlNetCoreMediaCapture captures = new(g);
            if (!captures.TryGetCapturedControlVideo(controlNetIndex, out WGNodeData video))
            {
                WarnUnavailableReferenceSource(reference.Source, descriptor);
                return null;
            }
            using WorkflowBridge bridge = BridgeSync.For(g);
            ImageFromBatchNode first = bridge.AddNode(
                new ImageFromBatchNode().With(BatchIndex: 0, Length: 1));
            first.Image.ConnectFromPath(bridge, video.Path);
            return WorkflowBridge.ToPath(first.IMAGE);
        }
        if (!StringUtils.Equals(reference.Source, MediaSource.Upload))
        {
            WGNodeData captured = ResolveHostCapture(reference.Source, $"{descriptor} {index}");
            if (captured is null)
            {
                RequestWarnings.Track(
                    g.UserInput,
                    $"VideoStages: {descriptor} {index} was dropped, so every later image "
                        + "reference on this clip moves down one <Picture> number.");
            }
            return captured?.Path;
        }
        return g.LoadImage(
            UploadedMedia.GetRefImage(
                g.UserInput,
                reference.Media.Data,
                reference.Media.FileName,
                $"{descriptor} {index}"),
            "${videostagesminimaxrefimage}",
            false).Path;
    }

    private WGNodeData ResolveReferenceVideo(
        MiniMaxReferencePlan reference,
        string descriptor)
    {
        if (StringUtils.Equals(reference.Source, MediaSource.Upload))
        {
            return g.LoadImage(
                UploadedMedia.GetVideo(
                    g.UserInput,
                    reference.Media.Data,
                    reference.Media.FileName,
                    descriptor),
                "${videostagesminimaxrefvideo}",
                resize: false);
        }
        if (MediaSource.TryParseControlNetIndex(reference.Source, out int controlNetIndex)
            && new ControlNetCoreMediaCapture(g).TryGetCapturedControlVideo(
                controlNetIndex,
                out WGNodeData video))
        {
            return video;
        }
        WarnUnavailableReferenceSource(reference.Source, descriptor);
        return null;
    }

    private JArray ResolveReferenceAudio(
        MiniMaxReferencePlan reference,
        string descriptor)
    {
        if (StringUtils.Equals(reference.Source, MediaSource.Upload))
        {
            return new JArray(
                g.CreateAudioLoadNode(
                    UploadedMedia.GetAudio(g.UserInput, reference.Media),
                    "${videostagesminimaxrefaudio}"),
                0);
        }
        if (MediaSource.TryParseControlNetIndex(reference.Source, out int controlNetIndex)
            && new ControlNetCoreMediaCapture(g).TryGetCapturedAudio(
                controlNetIndex,
                out WGNodeData controlNetAudio))
        {
            return controlNetAudio.Path as JArray;
        }
        if (MediaSource.TryParseAceStepFunIndex(reference.Source, out int trackIndex))
        {
            WGNodeData aceStepFunAudio = new AudioHandler(g).DetectAceStepFunAudio(trackIndex);
            if (aceStepFunAudio is not null)
            {
                return aceStepFunAudio.Path as JArray;
            }
        }
        WarnUnavailableReferenceSource(reference.Source, descriptor);
        return null;
    }

    private void WarnUnavailableReferenceSource(string source, string descriptor) =>
        RequestWarnings.Track(
            g.UserInput,
            $"VideoStages: {descriptor} source '{source}' was not available; "
                + "ignoring it for this generation.");

    /// <summary>
    /// Falls back to the request-global final frame only on a single-clip timeline that authored
    /// no reference of its own; more clips leave it no unambiguous target.
    /// </summary>
    private WGNodeData ResolveEndFrame(NativeFrameReferencePlan authored)
    {
        if (authored is not null)
        {
            return ResolveFrameReference(authored, "MiniMax H3 final keyframe");
        }
        Image global = plan.Clips.Count == 1
            ? g.UserInput.Get(T2IParamTypes.VideoEndImage, null)
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
            Width = UsesPlannedRootDimensions(stage)
                ? _dimensions.Width
                : g.CurrentMedia?.Width ?? _dimensions.Width,
            Height = UsesPlannedRootDimensions(stage)
                ? _dimensions.Height
                : g.CurrentMedia?.Height ?? _dimensions.Height,
            Prompt = positive,
            NegativePrompt = negative,
            Steps = stage.Core.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.StageId,
            ContextID = sectionId,
        };
    }

    private static bool UsesPlannedRootDimensions(StagePlan stage) =>
        stage.Input is StageInputKind.RootMedia or StageInputKind.EmptyLatent;
}
