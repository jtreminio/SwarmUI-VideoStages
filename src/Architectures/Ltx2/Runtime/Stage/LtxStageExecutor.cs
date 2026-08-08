using ComfyTyped.Core;
using ComfyTyped.Families;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Execution.Graph;
using VideoStages.Architectures.Ltx2.Runtime.Chain;
using VideoStages.Architectures.Ltx2.Runtime.Guide;

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
        double guideMergeStrength)
    {
        WorkflowGenerator.ImageToVideoGenInfo genInfo = stageContext.GenInfo;
        WGNodeData sourceMedia = stageContext.SourceMedia;
        LtxPostVideoChain postVideoChain = stageContext.PostVideoChain;
        bool incomingIcLoraMediaIncludesContinueHandle =
            stageContext.ClipContext.ContinueHandleMaterialized;
        postVideoChain?.AttachSourceAudio(sourceMedia);
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
            WGNodeData effectiveSourceMedia = g.CurrentMedia ?? sourceMedia;
            modelPromptPreparer.Prepare(genInfo, stageContext, effectiveSourceMedia);
            bool canReuseLatent =
                postVideoChain?.CanReuseCurrentOutputAsStageInput(effectiveSourceMedia) == true;
            if (!canReuseLatent && resolveFallbackGuide is not null)
            {
                guideMedia = resolveFallbackGuide();
            }

            WGNodeData stageLatent = latentBuilder.Build(
                genInfo,
                stageContext,
                effectiveSourceMedia,
                postVideoChain);
            if (stageLatent is null)
            {
                genInfo.PrepFullCond(g, guideMedia);
            }
            else
            {
                // Must stay below the latent build: defaulting Frames here would make the builder's
                // "no frame count" branch — the one that lands on PrepFullCond — unreachable.
                ApplyStageRuntimeDefaults(genInfo, effectiveSourceMedia);
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
                conditioning.UpscaleLatentIfPlanned(effectiveSourceMedia);
                conditioning.MergeOpeningFrameGuides(frameRefs);
                conditioning.BindToCurrentMedia(guideMedia, guideMergeStrength);
                // The continue tail merges after the other head conditioning so it wins.
                conditioning.MergeContinuityAnchor();
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
            outputFinalizer.Complete(genInfo, stageContext, postVideoChain,
                stageContext.RequiresDedicatedOutput || forceDedicatedOutput);
        }
        finally
        {
            g.IsImageToVideo = false;
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
