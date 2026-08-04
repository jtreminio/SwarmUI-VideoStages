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
    private const double DefaultGuideMergeStrength = 1.0;

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
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        Func<WGNodeData> resolveFallbackGuide,
        LtxPostVideoChainCapture postVideoChain,
        Action<WorkflowGenerator.ImageToVideoGenInfo> applyIcLora,
        IReadOnlyList<ResolvedClipRef> clipRefs = null,
        double guideMergeStrength = DefaultGuideMergeStrength)
    {
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

            // Claimed before the latent is built, which is the earliest of the three nodes.
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
                    .WithInplaceMerges(clipRefs ?? [])
                    .BindToCurrentMedia(
                        guideMedia,
                        guideMergeStrength)
                    .WithContinuityAnchor()
                    .WithLtxvConditioning()
                    .WithGuideAdditions(clipRefs ?? []);
            }
            genInfo.VideoCFG ??= genInfo.DefaultCFG;
            applyIcLora(genInfo);

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
                        == postVideoChain.State.VideoDecodeNodeId;
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

}
