using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages.LTX2;

internal sealed class LtxConditioningPipeline(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        LtxStageExecutor executor)
{
    private WGNodeData stageLatent;

    public LtxConditioningPipeline WithLatent(WGNodeData stageLatent, WGNodeData sourceMedia)
    {
        this.stageLatent = stageLatent;
        executor.ApplyResolvedFpsToWorkflow(genInfo, executor.ResolveFps(genInfo, sourceMedia));
        genInfo.VideoFPS ??= LtxStageExecutor.DefaultFpsValue;
        genInfo.Frames ??= LtxStageExecutor.DefaultFrameCountValue;
        genInfo.DefaultCFG = LtxStageExecutor.DefaultCfgValue;
        genInfo.HadSpecialCond = true;
        genInfo.DefaultSampler = LtxStageExecutor.DefaultSamplerValue;
        genInfo.DefaultScheduler = LtxStageExecutor.DefaultSchedulerValue;
        return this;
    }

    public LtxConditioningPipeline WithUpscaleIfNeeded(WGNodeData sourceMedia)
    {
        StageSpec stage = stageFrame.Stage;
        if (stage.Upscale <= 1 || string.IsNullOrWhiteSpace(stage.UpscaleMethod))
        {
            return this;
        }

        int baseWidth = Math.Max(sourceMedia?.Width ?? g.UserInput.GetImageWidth(), 16);
        int baseHeight = Math.Max(sourceMedia?.Height ?? g.UserInput.GetImageHeight(), 16);
        (int width, int height) = GetUpscaledDimensions(baseWidth, baseHeight, stage.Upscale);

        if (stage.UpscaleMethod.StartsWith("latentmodel-", StringComparison.OrdinalIgnoreCase))
        {
            string modelName = stage.UpscaleMethod["latentmodel-".Length..];
            stageLatent = ApplyLatentModelUpscale(modelName, width, height);
            stageFrame.ClipContext.Dimensions.Width = width;
            stageFrame.ClipContext.Dimensions.Height = height;
            return this;
        }

        Logs.Warning(
            $"VideoStages: Stage {stage.Id} uses unsupported LTX upscale method '{stage.UpscaleMethod}'. "
            + "Ignoring upscale.");
        return this;
    }

    public LtxConditioningPipeline WithInplaceMerges(IReadOnlyList<ResolvedClipRef> clipRefs)
    {
        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (!UseLtxvInplaceForRef(clipRef.Spec) || clipRef.Strength <= 0)
            {
                continue;
            }

            JArray preprocessed = executor.ResolvePreprocessedGuidePath(clipRef.Image.Path, stageLatent);
            string imgToVideoNode = executor.CreateLtxvImgToVideoInplaceNode(
                genInfo.Vae.Path,
                preprocessed,
                stageLatent.Path,
                clipRef.Strength,
                bypass: false);
            stageLatent = stageLatent.WithPath(
                [imgToVideoNode, 0],
                WGNodeData.DT_LATENT_VIDEO,
                genInfo.Model.Compat);
        }

        return this;
    }

    public LtxConditioningPipeline BindToCurrentMedia(
        bool skipGuideReinjection,
        WGNodeData guideMedia,
        double guideMergeStrength)
    {
        if (skipGuideReinjection || guideMergeStrength <= 0)
        {
            g.CurrentMedia = stageLatent;
            return this;
        }

        JArray preprocessedGuidePath = executor.ResolvePreprocessedGuidePath(guideMedia.Path, stageLatent);
        string imgToVideoNode = executor.CreateLtxvImgToVideoInplaceNode(
            genInfo.Vae.Path,
            preprocessedGuidePath,
            stageLatent.Path,
            guideMergeStrength,
            bypass: false);
        g.CurrentMedia = stageLatent.WithPath(
            [imgToVideoNode, 0],
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat);
        return this;
    }

    public LtxConditioningPipeline WithLtxvConditioning()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LTXVConditioningNode cond = bridge.AddNode(new LTXVConditioningNode());
        if (genInfo.VideoFPS.HasValue)
        {
            cond.FrameRate.Set(genInfo.VideoFPS.Value);
        }
        cond.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond);
        cond.NegativeInput.ConnectFromPath(bridge, genInfo.NegCond);
        bridge.SyncNode(cond);
        BridgeSync.SyncLastId(g);

        genInfo.PosCond = [cond.Id, 0];
        genInfo.NegCond = [cond.Id, 1];
        return this;
    }

    public LtxConditioningPipeline WithGuideAdditions(IReadOnlyList<ResolvedClipRef> clipRefs)
    {
        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (UseLtxvInplaceForRef(clipRef.Spec) || clipRef.Strength <= 0)
            {
                continue;
            }

            JArray preprocessed = executor.ResolvePreprocessedGuidePath(clipRef.Image.Path, g.CurrentMedia);
            int frameIdx = ComputeLtxvAddGuideFrameIndex(clipRef.Spec);

            WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            LTXVAddGuideNode addGuide = bridge.AddNode(new LTXVAddGuideNode()).With(
                FrameIdx: frameIdx,
                Strength: clipRef.Strength);
            addGuide.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond);
            addGuide.NegativeInput.ConnectFromPath(bridge, genInfo.NegCond);
            addGuide.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
            addGuide.LatentInput.ConnectFromPath(bridge, g.CurrentMedia.Path);
            addGuide.Image.ConnectFromPath(bridge, preprocessed);
            bridge.SyncNode(addGuide);
            BridgeSync.SyncLastId(g);

            stageFrame.NeedsCropGuidesAfterSampler = true;
            genInfo.PosCond = [addGuide.Id, 0];
            genInfo.NegCond = [addGuide.Id, 1];
            g.CurrentMedia = g.CurrentMedia.WithPath(
                [addGuide.Id, 2],
                WGNodeData.DT_LATENT_VIDEO,
                genInfo.Model.Compat);
        }

        return this;
    }

    private static (int Width, int Height) GetUpscaledDimensions(int baseWidth, int baseHeight, double upscale)
    {
        int width = AlignTo16((int)Math.Round(baseWidth * upscale));
        int height = AlignTo16((int)Math.Round(baseHeight * upscale));
        return (width, height);
    }

    private static int AlignTo16(int value) => Math.Max(16, Math.Max(value, 16) / 16 * 16);

    private WGNodeData ApplyLatentModelUpscale(string modelName, int width, int height)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LatentUpscaleModelLoaderNode loader = bridge.AddNode(new LatentUpscaleModelLoaderNode()).With(
            ModelName: modelName);
        bridge.SyncNode(loader);

        LTXVLatentUpsamplerNode upsampler = bridge.AddNode(new LTXVLatentUpsamplerNode());
        upsampler.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
        upsampler.Samples.ConnectFromPath(bridge, stageLatent.Path);
        upsampler.UpscaleModel.ConnectTo(loader.LATENTUPSCALEMODEL);
        bridge.SyncNode(upsampler);
        BridgeSync.SyncLastId(g);

        WGNodeData upscaled = stageLatent.WithPath([upsampler.Id, 0], WGNodeData.DT_LATENT_VIDEO);
        upscaled.Width = width;
        upscaled.Height = height;
        return upscaled;
    }

    private static bool UseLtxvInplaceForRef(ImageRefSpec spec) => !spec.FromEnd && spec.Frame == 1;

    private static int ComputeLtxvAddGuideFrameIndex(ImageRefSpec spec)
        => spec.FromEnd ? -Math.Max(1, spec.Frame) : Math.Max(1, spec.Frame);
}
