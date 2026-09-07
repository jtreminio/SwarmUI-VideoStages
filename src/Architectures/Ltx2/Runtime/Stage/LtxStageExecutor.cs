using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Architectures.Ltx2.Runtime.Chain;
using VideoStages.Architectures.Ltx2.Runtime.Guide;
using VideoStages.CharacterRefine;
using VideoStages.Execution;
using VideoStages.Execution.Graph;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Runtime.Stage;

internal sealed record ResolvedFrameRef(
    WGNodeData Image,
    FrameRefPlan Reference,
    double Strength);

internal sealed class LtxStageExecutor
{
    private readonly WorkflowGenerator g;
    private readonly RootVideoStageResizer rootVideoStageResizer;
    private readonly LtxModelPromptPreparer modelPromptPreparer;
    private readonly LtxStageLatentBuilder latentBuilder;
    private readonly LtxStageSampler sampler;
    private readonly LtxStageOutputFinalizer outputFinalizer;
    private readonly HostRootAdoption rootAdoption;

    internal LtxStageExecutor(
        WorkflowGenerator g,
        RootVideoStageResizer rootVideoStageResizer,
        HostRootAdoption rootAdoption)
    {
        this.g = g;
        this.rootVideoStageResizer = rootVideoStageResizer;
        this.rootAdoption = rootAdoption;
        modelPromptPreparer = new LtxModelPromptPreparer(g);
        latentBuilder = new LtxStageLatentBuilder(g);
        sampler = new LtxStageSampler(g);
        outputFinalizer = new LtxStageOutputFinalizer(g);
    }

    public void RunStage(
        StageContext stageContext,
        WGNodeData guideMedia,
        Func<WGNodeData> resolveFallbackGuide,
        IReadOnlyList<ResolvedFrameRef> frameRefs,
        double guideMergeStrength,
        JArray characterIdentityImage = null)
    {
        WorkflowGenerator.ImageToVideoGenInfo genInfo = stageContext.GenInfo;
        WGNodeData sourceMedia = stageContext.SourceMedia;
        LtxPostVideoChain postVideoChain = stageContext.PostVideoChain;
        LtxCharacterRefinePlan characterRefine =
            stageContext.Stage.RequireLtx2Payload().CharacterRefine;
        bool incomingIcLoraMediaIncludesContinueHandle =
            stageContext.ClipContext.ContinueHandleMaterialized;
        bool ambientImageToVideo = g.IsImageToVideo;
        g.IsImageToVideo = true;

        try
        {
            foreach (
                Action<WorkflowGenerator.ImageToVideoGenInfo> handler in
                WorkflowGenerator.AltImageToVideoPreHandlers)
            {
                handler(genInfo);
            }

            stageContext.Claim = rootAdoption.ClaimTextRoot(
                stageContext.ClipContext.PlannedClip,
                stageContext.Stage,
                includeLatent: true,
                includeConditioning: true);
            BonitaFindAndCropNode characterCrop = null;
            int characterSourceFrames = 0;
            WGNodeData effectiveSourceMedia;
            if (characterRefine is null)
            {
                effectiveSourceMedia = g.CurrentMedia ?? sourceMedia;
            }
            else
            {
                genInfo.Seed = VideoSamplerSeed.FromSourceSampler(
                    g,
                    sourceMedia,
                    genInfo.Seed);
                int startStep = StageStartStepPolicy.StartStep(
                    stageContext.Stage.Core.Steps,
                    stageContext.Stage.Core.Control);
                if (sourceMedia?.DataType != WGNodeData.DT_VIDEO
                    || sourceMedia.Path is not JArray)
                {
                    throw Invariant.Failure(
                        $"Make Them Bonita stage {stageContext.Stage.StageId} requires decoded video.");
                }
                characterSourceFrames = sourceMedia.Frames
                    ?? throw Invariant.Failure(
                        $"Make Them Bonita stage {stageContext.Stage.StageId} has no input frame count.");
                if (startStep >= stageContext.Stage.Core.Steps)
                {
                    g.CurrentMedia = sourceMedia;
                    return;
                }
                if (characterRefine.IdentityReferences.Count > 0)
                {
                    BonitaIntegration.EnsureBuffaloModel(g);
                }
                (characterCrop, effectiveSourceMedia) = BonitaGraph.Crop(
                    g,
                    sourceMedia,
                    characterRefine.Settings,
                    characterIdentityImage);
                int samplingFrames = StaticGeneratedFrameGrid.SnapUp(
                    characterSourceFrames,
                    Ltx2ArchitectureModule.FrameGrid);
                if (samplingFrames != characterSourceFrames)
                {
                    using WorkflowBridge bridge = BridgeSync.For(g);
                    SwarmFrameWindowNode pad = bridge.AddNode(
                        new SwarmFrameWindowNode().With(
                            StartFrame: 0,
                            FrameCount: samplingFrames));
                    pad.ImagesInput.ConnectFromPath(bridge, effectiveSourceMedia.Path);
                    effectiveSourceMedia = effectiveSourceMedia.WithPath(pad.Images.ToPath());
                }
                effectiveSourceMedia.Frames = samplingFrames;
                effectiveSourceMedia.FPS = sourceMedia.GetRawFPS();
                genInfo.Width = characterRefine.Settings.CropWidth;
                genInfo.Height = characterRefine.Settings.CropHeight;
                genInfo.Frames = samplingFrames;
                genInfo.StartStep = startStep;
                postVideoChain = null;
            }
            modelPromptPreparer.Prepare(genInfo, stageContext, effectiveSourceMedia);
            if (stageContext.ClipContext.PlannedClip.AudioOnly)
            {
                ExecuteAudioOnly(genInfo, stageContext);
                return;
            }
            if (postVideoChain is not null)
            {
                if (!postVideoChain.MatchesLoadedVaes(
                        genInfo.VideoModel,
                        genInfo.Vae,
                        g.CurrentAudioVae))
                {
                    postVideoChain = null;
                    stageContext.PostVideoChain = null;
                }
                else
                {
                    postVideoChain.AttachSourceAudio(sourceMedia);
                    if (!ReferenceEquals(sourceMedia, effectiveSourceMedia))
                    {
                        postVideoChain.AttachSourceAudio(effectiveSourceMedia);
                    }
                }
            }
            bool canReuseLatent =
                postVideoChain?.CanReuseCurrentOutputAsStageInput(effectiveSourceMedia) == true;
            if (!canReuseLatent && resolveFallbackGuide is not null)
            {
                guideMedia = resolveFallbackGuide();
            }

            WGNodeData stageLatent;
            if (characterRefine is null)
            {
                stageLatent = latentBuilder.Build(
                    genInfo,
                    stageContext,
                    effectiveSourceMedia,
                    postVideoChain);
            }
            else
            {
                ApplyStageRuntimeDefaults(genInfo, effectiveSourceMedia);
                stageLatent = effectiveSourceMedia.AsLatentImage(genInfo.Vae);
                stageLatent.Frames = genInfo.Frames;
                stageLatent.FPS = genInfo.VideoFPS;
                stageLatent.AttachedAudio = null;
                stageLatent = stageLatent.EnsureHasAudioIfNeeded(
                    genInfo.Vae,
                    g.CurrentAudioVae);
            }
            if (stageLatent is null)
            {
                genInfo.PrepFullCond(g, guideMedia);
            }
            else
            {
                // Must stay below the latent build: defaulting Frames here would make the builder's
                // "no frame count" branch — the one that lands on PrepFullCond — unreachable.
                if (characterRefine is null)
                {
                    ApplyStageRuntimeDefaults(genInfo, effectiveSourceMedia);
                }
                LtxConditioningPipeline conditioning = new(
                    g,
                    genInfo,
                    stageContext,
                    new LtxGuidePreprocessReuse(
                        g,
                        rootVideoStageResizer,
                        stageContext.ClipContext.PlannedClip
                            .RequireLtx2Payload()
                            .ReferenceFraming),
                    stageLatent);
                if (characterRefine is null)
                {
                    conditioning.UpscaleLatentIfPlanned(effectiveSourceMedia);
                }
                conditioning.MergeOpeningFrameGuides(frameRefs);
                conditioning.BindToCurrentMedia(guideMedia, guideMergeStrength);
                if (characterRefine is null)
                {
                    // The continue tail merges after the other head conditioning so it wins.
                    conditioning.MergeContinuityAnchor();
                }
                conditioning.AddLtxvConditioning();
                conditioning.AddGuideConditioning(frameRefs);
            }
            genInfo.VideoCFG ??= genInfo.DefaultCFG;
            WGNodeData incomingMedia = ResolveIcLoraStageInput(stageContext);
            stageContext.NeedsCropGuidesAfterSampler |=
                new IcLoraApplicator(g).ApplyIcLoras(
                    genInfo,
                    stageContext.ClipContext.PlannedClip,
                    stageContext.Stage,
                    genInfo.Frames,
                    incomingMedia,
                    stageContext.ClipContext.IncomingContinueHandleFrames,
                    incomingIcLoraMediaIncludesContinueHandle);
            new IcLoraAudioReferenceApplicator(g).ApplyAudioReferenceTokens(
                genInfo,
                stageContext.ClipContext.PlannedClip,
                stageContext,
                incomingMedia);

            foreach (
                Action<WorkflowGenerator.ImageToVideoGenInfo> handler in
                WorkflowGenerator.AltImageToVideoPostHandlers)
            {
                handler(genInfo);
            }

            // When this stage re-encoded the chain's own decoded pixels, splicing its output back
            // onto that decode in place would wire the decode to its own downstream encode.
            bool forceDedicatedOutput = false;
            if (!canReuseLatent
                && postVideoChain is { HasPostDecodeWrappers: false }
                && effectiveSourceMedia?.Path is not null)
            {
                using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
                ComfyNode sourceNode = bridge.ResolvePath(effectiveSourceMedia.Path)?.Node;
                forceDedicatedOutput = sourceNode is not null
                    && bridge.Graph.FindNearestUpstream<IVaeDecode>(sourceNode)?.Id
                        == postVideoChain.VideoDecodeNodeId;
            }

            sampler.Execute(genInfo, stageContext);
            if (characterRefine is null)
            {
                outputFinalizer.Complete(genInfo, stageContext, postVideoChain,
                    stageContext.RequiresDedicatedOutput || forceDedicatedOutput);
            }
            else
            {
                WGNodeData decoded = VaeDecodePreference.AsRawImage(
                    g,
                    g.CurrentMedia,
                    genInfo.Vae);
                JArray repairedPath = decoded.Path;
                if (genInfo.Frames != characterSourceFrames)
                {
                    using WorkflowBridge bridge = BridgeSync.For(g);
                    SwarmFrameWindowNode trim = bridge.AddNode(
                        new SwarmFrameWindowNode().With(
                            StartFrame: 0,
                            FrameCount: characterSourceFrames));
                    trim.ImagesInput.ConnectFromPath(bridge, repairedPath);
                    repairedPath = trim.Images.ToPath();
                }
                g.CurrentMedia = BonitaGraph.Apply(
                    g,
                    sourceMedia,
                    characterCrop,
                    repairedPath);
                LtxPostVideoChain.ExcludeFromCapture(g, g.CurrentMedia);
                g.CurrentMedia.Width = sourceMedia.Width;
                g.CurrentMedia.Height = sourceMedia.Height;
                g.CurrentMedia.Frames = characterSourceFrames;
                g.CurrentMedia.FPS = sourceMedia.GetRawFPS();
                g.CurrentMedia.AttachedAudio = sourceMedia.AttachedAudio?.Duplicate();
                g.CurrentVae = genInfo.Vae;
            }
        }
        finally
        {
            g.IsImageToVideo = ambientImageToVideo;
        }
    }

    private void ApplyStageRuntimeDefaults(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia)
    {
        LtxStageRuntimeSettings.ApplyResolvedFpsToWorkflow(
            g,
            genInfo,
            LtxStageRuntimeSettings.ResolveFps(g, genInfo, sourceMedia));
        genInfo.VideoFPS ??= LtxStageRuntimeSettings.DefaultFps;
        genInfo.Frames ??= LtxStageRuntimeSettings.DefaultFrameCount;
        genInfo.DefaultCFG = LtxStageRuntimeSettings.DefaultCfg;
        genInfo.HadSpecialCond = true;
        genInfo.DefaultSampler = LtxStageRuntimeSettings.DefaultSampler;
        genInfo.DefaultScheduler = LtxStageRuntimeSettings.DefaultScheduler;
    }

    private void ExecuteAudioOnly(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageContext stageContext)
    {
        ApplyStageRuntimeDefaults(genInfo, null);
        genInfo.Width = Constants.AudioOnlyDimension;
        genInfo.Height = Constants.AudioOnlyDimension;
        if (g.CurrentAudioVae?.Path is not JArray)
        {
            throw Invariant.Failure("LTX audio-only generation loaded no audio VAE.");
        }
        genInfo.PrepFullCond(g, null);
        using (WorkflowBridge bridge = BridgeSync.For(g))
        {
            LTXVAudioOnlyModelNode audioOnlyModel = bridge.AddNode(
                new LTXVAudioOnlyModelNode());
            audioOnlyModel.ModelInput.ConnectFromPath(bridge, genInfo.Model.Path);
            genInfo.Model = genInfo.Model.WithPath(audioOnlyModel.Model.ToPath());

            LTXVEmptyLatentAudioNode audio = bridge.AddNode(
                new LTXVEmptyLatentAudioNode().With(
                    FramesNumber: genInfo.Frames.Value,
                    BatchSize: 1));
            audio.FrameRate.Set(genInfo.VideoFPS.Value);
            audio.AudioVae.ConnectFromPath(bridge, g.CurrentAudioVae.Path);
            EmptyLTXVLatentVideoNode video = bridge.Graph
                .NodesOfType<EmptyLTXVLatentVideoNode>()
                .Single(node =>
                    node.Width.LiteralAsInt() == Constants.AudioOnlyDimension
                    && node.Height.LiteralAsInt() == Constants.AudioOnlyDimension);
            video.Length.Set(1);
            LTXVConcatAVLatentNode joint = bridge.AddNode(
                new LTXVConcatAVLatentNode().With(
                    VideoLatent: video.LATENT,
                    AudioLatent: audio.Latent));
            g.CurrentMedia = new(
                joint.Latent.ToPath(),
                g,
                WGNodeData.DT_LATENT_AUDIOVIDEO,
                genInfo.Model.Compat)
            {
                Width = Constants.AudioOnlyDimension,
                Height = Constants.AudioOnlyDimension,
                Frames = genInfo.Frames,
                FPS = genInfo.VideoFPS,
            };
        }
        sampler.Execute(genInfo, stageContext);

        using WorkflowBridge decodeBridge = BridgeSync.For(g);
        LTXVSeparateAVLatentNode separate = decodeBridge.AddNode(
            new LTXVSeparateAVLatentNode());
        separate.AvLatent.ConnectFromPath(decodeBridge, g.CurrentMedia.Path);
        LTXVAudioVAEDecodeNode decode = decodeBridge.AddNode(
            new LTXVAudioVAEDecodeNode().With(Samples: separate.AudioLatent));
        decode.AudioVae.ConnectFromPath(decodeBridge, g.CurrentAudioVae.Path);
        g.CurrentMedia = new(
            decode.Audio.ToPath(),
            g,
            WGNodeData.DT_AUDIO,
            g.CurrentAudioVae.Compat);
        g.CurrentVae = null;
    }

    private WGNodeData ResolveIcLoraStageInput(StageContext stageContext)
    {
        bool wantsIncoming = stageContext.Stage.RequireLtx2Payload().IcLoras.Any(entry =>
            entry.Drive.Source == IcLoraMediaSourceKind.Incoming);
        if (!wantsIncoming)
        {
            return null;
        }
        WGNodeData source = stageContext.ClipContext.IsFirstStage(stageContext.Stage)
            ? stageContext.ClipContext.IcLoraEntryIncomingMedia
            : stageContext.SourceMedia;
        LtxPostVideoChain postVideoChain = stageContext.PostVideoChain;
        if (postVideoChain is null || !postVideoChain.ReferencesOutput(source))
        {
            return source;
        }
        WGNodeData detached = postVideoChain.CreateDetachedGuideMedia(g.CurrentVae);
        if (detached is null)
        {
            return source;
        }
        detached.AttachedAudio = source?.AttachedAudio;
        return detached;
    }

}
