using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Generated;

namespace VideoStages.LTX2;

internal sealed class LtxConditioningPipeline
{
    private readonly WorkflowGenerator g;
    private readonly WorkflowGenerator.ImageToVideoGenInfo genInfo;
    private readonly StageFrame stageFrame;
    private readonly LtxStageExecutor executor;

    private WGNodeData stageLatent;

    public LtxConditioningPipeline(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        LtxStageExecutor executor)
    {
        this.g = g;
        this.genInfo = genInfo;
        this.stageFrame = stageFrame;
        this.executor = executor;
    }

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

    public LtxConditioningPipeline WithUpscaleIfNeeded(JsonParser.StageSpec stage, WGNodeData sourceMedia)
    {
        if (stage.Upscale == 1 || string.IsNullOrWhiteSpace(stage.UpscaleMethod))
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
            return this;
        }

        if (stage.UpscaleMethod.StartsWith("latent-", StringComparison.OrdinalIgnoreCase))
        {
            string latentMethod = stage.UpscaleMethod["latent-".Length..];
            stageLatent = ApplyLatentUpscale(latentMethod, stage.Upscale, width, height);
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
            if (!UseLtxvInplaceForRef(clipRef.Spec))
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
        if (skipGuideReinjection)
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
        if (bridge.ResolvePath(genInfo.PosCond) is INodeOutput pos)
        {
            cond.PositiveInput.ConnectToUntyped(pos);
        }
        if (bridge.ResolvePath(genInfo.NegCond) is INodeOutput neg)
        {
            cond.NegativeInput.ConnectToUntyped(neg);
        }
        if (genInfo.VideoFPS.HasValue)
        {
            cond.FrameRate.Set((double)genInfo.VideoFPS.Value);
        }
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
            if (UseLtxvInplaceForRef(clipRef.Spec))
            {
                continue;
            }

            JArray preprocessed = executor.ResolvePreprocessedGuidePath(clipRef.Image.Path, g.CurrentMedia);
            int frameIdx = ComputeLtxvAddGuideFrameIndex(clipRef.Spec);

            WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            LTXVAddGuideNode addGuide = bridge.AddNode(new LTXVAddGuideNode());
            if (bridge.ResolvePath(genInfo.PosCond) is INodeOutput pos)
            {
                addGuide.PositiveInput.ConnectToUntyped(pos);
            }
            if (bridge.ResolvePath(genInfo.NegCond) is INodeOutput neg)
            {
                addGuide.NegativeInput.ConnectToUntyped(neg);
            }
            if (genInfo.Vae?.Path is JArray vaePath
                && bridge.ResolvePath(vaePath) is INodeOutput vae)
            {
                addGuide.Vae.ConnectToUntyped(vae);
            }
            if (g.CurrentMedia?.Path is JArray latentPath
                && bridge.ResolvePath(latentPath) is INodeOutput latent)
            {
                addGuide.LatentInput.ConnectToUntyped(latent);
            }
            if (bridge.ResolvePath(preprocessed) is INodeOutput img)
            {
                addGuide.Image.ConnectToUntyped(img);
            }
            addGuide.FrameIdx.Set(frameIdx);
            addGuide.Strength.Set(clipRef.Strength);
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

    private static int AlignTo16(int value)
    {
        return Math.Max(16, (Math.Max(value, 16) / 16) * 16);
    }

    private WGNodeData ApplyLatentModelUpscale(string modelName, int width, int height)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LatentUpscaleModelLoaderNode loader = bridge.AddNode(new LatentUpscaleModelLoaderNode());
        loader.ModelName.Set(modelName);
        bridge.SyncNode(loader);

        LTXVLatentUpsamplerNode upsampler = bridge.AddNode(new LTXVLatentUpsamplerNode());
        if (genInfo.Vae?.Path is JArray vaePath
            && bridge.ResolvePath(vaePath) is INodeOutput vae)
        {
            upsampler.Vae.ConnectToUntyped(vae);
        }
        if (bridge.ResolvePath(stageLatent.Path) is INodeOutput samples)
        {
            upsampler.Samples.ConnectToUntyped(samples);
        }
        upsampler.UpscaleModel.ConnectTo(loader.LATENTUPSCALEMODEL);
        bridge.SyncNode(upsampler);
        BridgeSync.SyncLastId(g);

        WGNodeData upscaled = stageLatent.WithPath([upsampler.Id, 0], WGNodeData.DT_LATENT_VIDEO);
        upscaled.Width = width;
        upscaled.Height = height;
        return upscaled;
    }

    private WGNodeData ApplyLatentUpscale(string latentMethod, double scaleBy, int width, int height)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LatentUpscaleByNode node = bridge.AddNode(new LatentUpscaleByNode());
        if (bridge.ResolvePath(stageLatent.Path) is INodeOutput samples)
        {
            node.Samples.ConnectToUntyped(samples);
        }
        node.UpscaleMethod.Set(latentMethod);
        node.ScaleBy.Set(scaleBy);
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);

        WGNodeData upscaled = stageLatent.WithPath([node.Id, 0], WGNodeData.DT_LATENT_VIDEO);
        upscaled.Width = width;
        upscaled.Height = height;
        return upscaled;
    }

    private static bool UseLtxvInplaceForRef(JsonParser.RefSpec spec) => !spec.FromEnd && spec.Frame == 1;

    private static int ComputeLtxvAddGuideFrameIndex(JsonParser.RefSpec spec)
    {
        if (spec.FromEnd)
        {
            return -Math.Max(1, spec.Frame);
        }

        return Math.Max(1, spec.Frame);
    }
}
