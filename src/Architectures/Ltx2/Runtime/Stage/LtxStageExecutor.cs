using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
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
    private readonly IArchitectureRootMediaResizer rootVideoStageResizer;
    private readonly LtxStageRuntimeSettings runtimeSettings;
    private readonly LtxModelPromptPreparer modelPromptPreparer;
    private readonly LtxStageLatentBuilder latentBuilder;
    private readonly LtxStageSampler sampler;
    private readonly LtxStageOutputFinalizer outputFinalizer;

    internal LtxStageExecutor(
        WorkflowGenerator g,
        IArchitectureRootMediaResizer rootVideoStageResizer)
    {
        this.g = g;
        this.rootVideoStageResizer = rootVideoStageResizer;
        runtimeSettings = new LtxStageRuntimeSettings(g);
        modelPromptPreparer = new LtxModelPromptPreparer(g, runtimeSettings);
        latentBuilder = new LtxStageLatentBuilder(g, runtimeSettings);
        sampler = new LtxStageSampler(g);
        outputFinalizer = new LtxStageOutputFinalizer(g);
    }

    public void RunStage(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        bool skipGuideReinjection,
        LtxPostVideoChainCapture postVideoChain,
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

            WGNodeData effectiveSourceMedia = g.CurrentMedia ?? sourceMedia;
            modelPromptPreparer.Prepare(genInfo, stageFrame, effectiveSourceMedia);

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
                        runtimeSettings,
                        new LtxGuidePreprocessReuse(g, rootVideoStageResizer))
                    .WithLatent(stageLatent, effectiveSourceMedia)
                    .WithUpscaleIfNeeded(effectiveSourceMedia)
                    .WithInplaceMerges(clipRefs ?? [])
                    .BindToCurrentMedia(
                        skipGuideReinjection,
                        guideMedia,
                        guideMergeStrength)
                    .WithContinuityAnchor()
                    .WithLtxvConditioning()
                    .WithGuideAdditions(clipRefs ?? []);
            }
            genInfo.VideoCFG ??= genInfo.DefaultCFG;

            foreach (
                Action<WorkflowGenerator.ImageToVideoGenInfo> handler in
                WorkflowGenerator.AltImageToVideoPostHandlers)
            {
                handler(genInfo);
            }

            sampler.Execute(genInfo, stageFrame);
            outputFinalizer.Complete(
                genInfo,
                stageFrame,
                effectiveSourceMedia,
                postVideoChain);
        }
        finally
        {
            g.IsImageToVideo = false;
        }
    }
}
