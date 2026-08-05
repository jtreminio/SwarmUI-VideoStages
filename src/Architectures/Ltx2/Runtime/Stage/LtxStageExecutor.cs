using ComfyTyped.Core;
using ComfyTyped.Families;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed record ResolvedClipRef(
    WGNodeData Image,
    ImageReferencePlan Reference,
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
        StageFrame stageFrame,
        WGNodeData guideMedia,
        Func<WGNodeData> resolveFallbackGuide,
        IReadOnlyList<ResolvedClipRef> clipRefs,
        double guideMergeStrength)
    {
        WorkflowGenerator.ImageToVideoGenInfo genInfo = stageFrame.GenInfo;
        WGNodeData sourceMedia = stageFrame.SourceMedia;
        LtxPostVideoChainCapture postVideoChain = stageFrame.PostVideoChain;
        bool incomingIcLoraMediaIncludesContinueHandle =
            stageFrame.ClipContext.ContinueHandleMaterialized;
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

            stageFrame.Claim = rootAdoption.ClaimWholeTextRoot(
                stageFrame.ClipContext.PlannedClip,
                stageFrame.Stage);
            WGNodeData effectiveSourceMedia = g.CurrentMedia ?? sourceMedia;
            modelPromptPreparer.Prepare(genInfo, stageFrame, effectiveSourceMedia);
            bool canReuseLatent =
                postVideoChain?.CanReuseCurrentOutputAsStageInput(effectiveSourceMedia) == true;
            if (!canReuseLatent && resolveFallbackGuide is not null)
            {
                guideMedia = resolveFallbackGuide();
            }

            WGNodeData stageLatent = latentBuilder.Build(
                genInfo,
                stageFrame,
                effectiveSourceMedia,
                postVideoChain);
            if (stageLatent is null)
            {
                genInfo.PrepFullCond(g, guideMedia);
            }
            else
            {
                new LtxConditioningPipeline(
                        g,
                        genInfo,
                        stageFrame,
                        new LtxGuidePreprocessReuse(
                            g,
                            rootVideoStageResizer,
                            stageFrame.ClipContext.PlannedClip
                                .RequireLtx2Payload()
                                .ReferenceFraming))
                    .WithLatent(stageLatent, effectiveSourceMedia)
                    .WithUpscaleIfNeeded(effectiveSourceMedia)
                    .WithInplaceMerges(clipRefs)
                    .BindToCurrentMedia(
                        guideMedia,
                        guideMergeStrength)
                    .WithContinuityAnchor()
                    .WithLtxvConditioning()
                    .WithGuideAdditions(clipRefs);
            }
            genInfo.VideoCFG ??= genInfo.DefaultCFG;
            WGNodeData incomingMedia = ResolveIcLoraStageInput(stageFrame);
            stageFrame.NeedsCropGuidesAfterSampler |=
                new IcLoraApplicator(g).ApplyIcLoras(
                    genInfo,
                    stageFrame.ClipContext.PlannedClip,
                    stageFrame.Stage,
                    genInfo.Frames,
                    incomingMedia,
                    stageFrame.ClipContext.IncomingContinueHandleFrames,
                    incomingIcLoraMediaIncludesContinueHandle);
            new IcLoraAudioReferenceApplicator(g).ApplyAudioReferenceTokens(
                genInfo,
                stageFrame.ClipContext.PlannedClip,
                stageFrame,
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

            sampler.Execute(genInfo, stageFrame);
            outputFinalizer.Complete(genInfo, stageFrame, postVideoChain,
                stageFrame.RequiresDedicatedOutput || forceDedicatedOutput);
        }
        finally
        {
            g.IsImageToVideo = false;
        }
    }

    private WGNodeData ResolveIcLoraStageInput(StageFrame stageFrame)
    {
        bool wantsIncoming = stageFrame.Stage.RequireLtx2Payload().IcLoras.Any(entry =>
            entry.Drive.Source == IcLoraMediaSourceKind.Incoming);
        if (!wantsIncoming)
        {
            return null;
        }
        WGNodeData source = stageFrame.ClipContext.IsFirstStage(stageFrame.Stage)
            ? stageFrame.ClipContext.IcLoraEntryIncomingMedia
            : stageFrame.SourceMedia;
        LtxPostVideoChainCapture postVideoChain = stageFrame.PostVideoChain;
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
