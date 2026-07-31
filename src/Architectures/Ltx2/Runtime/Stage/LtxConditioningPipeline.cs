using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxConditioningPipeline(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        LtxStageRuntimeSettings runtimeSettings,
        LtxGuidePreprocessReuse guidePreprocessReuse)
{
    private WGNodeData stageLatent;

    /// <summary>
    /// When the stage carries an active retake, its per-frame noise mask IS the base lock (the latent
    /// already encodes the base footage). LTXVImgToVideoInplace overwrites the noise mask of every frame
    /// it conditions (<c>mask[:, :, :F] = 1-strength</c>), so any inplace merge would clobber the retake
    /// window — skip them all.
    /// </summary>
    private bool RetakeMaskActive => stageFrame.Stage.HasActiveRetakeMask();

    public LtxConditioningPipeline WithLatent(WGNodeData stageLatent, WGNodeData sourceMedia)
    {
        this.stageLatent = stageLatent;
        runtimeSettings.ApplyResolvedFpsToWorkflow(
            genInfo,
            runtimeSettings.ResolveFps(genInfo, sourceMedia));
        genInfo.VideoFPS ??= LtxStageRuntimeSettings.DefaultFps;
        genInfo.Frames ??= LtxStageRuntimeSettings.DefaultFrameCount;
        genInfo.DefaultCFG = LtxStageRuntimeSettings.DefaultCfg;
        genInfo.HadSpecialCond = true;
        genInfo.DefaultSampler = LtxStageRuntimeSettings.DefaultSampler;
        genInfo.DefaultScheduler = LtxStageRuntimeSettings.DefaultScheduler;
        return this;
    }

    public LtxConditioningPipeline WithUpscaleIfNeeded(WGNodeData sourceMedia)
    {
        StagePlan stage = stageFrame.Stage;
        StageUpscalePlan upscale = stage.Core.Upscale;
        if (upscale.Mode is not (StageUpscaleMode.LatentModel or StageUpscaleMode.Latent))
        {
            return this;
        }

        int baseWidth = Math.Max(sourceMedia?.Width ?? g.UserInput.GetImageWidth(), 16);
        int baseHeight = Math.Max(sourceMedia?.Height ?? g.UserInput.GetImageHeight(), 16);
        (int width, int height) =
            StageDimensionRules.ResolveUpscaled(stage, baseWidth, baseHeight);

        stageLatent = upscale.Mode == StageUpscaleMode.Latent
            ? ApplyLatentUpscale(upscale.MethodName, upscale.Factor, width, height)
            : ApplyLatentModelUpscale(upscale.MethodName, width, height);
        stageFrame.ClipContext.Dimensions.Width = width;
        stageFrame.ClipContext.Dimensions.Height = height;
        stageFrame.ClipContext.Dimensions.HasLatentUpscale = true;
        return this;
    }

    public LtxConditioningPipeline WithInplaceMerges(IReadOnlyList<ResolvedClipRef> clipRefs)
    {
        if (RetakeMaskActive)
        {
            return this;
        }
        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (!UseLtxvInplaceForRef(clipRef.Reference) || clipRef.Strength <= 0)
            {
                continue;
            }

            JArray preprocessed = ResolvePreprocessedGuidePath(clipRef.Image.Path, stageLatent);
            string imgToVideoNode = CreateLtxvImgToVideoInplaceNode(
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
        if (RetakeMaskActive || skipGuideReinjection || guideMergeStrength <= 0)
        {
            g.CurrentMedia = stageLatent;
            return this;
        }

        JArray preprocessedGuidePath = ResolvePreprocessedGuidePath(guideMedia.Path, stageLatent);
        string imgToVideoNode = CreateLtxvImgToVideoInplaceNode(
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

    /// <summary>
    /// Re-freezes a continue boundary's tail over the stage's opening frames. It runs after the stage's
    /// own input is bound so it wins over whatever conditioning covers the head, and it conditions only
    /// the tail's own frame count, leaving everything past the overlap window as the handoff left it.
    /// The opening stage of a clip takes the tail as its primary guide instead and skips this.
    /// </summary>
    public LtxConditioningPipeline WithContinuityAnchor()
    {
        if (stageFrame.ContinuityAnchor is null || RetakeMaskActive)
        {
            return this;
        }

        JArray preprocessed = ResolvePreprocessedGuidePath(
            stageFrame.ContinuityAnchor.Path,
            g.CurrentMedia);
        string imgToVideoNode = CreateLtxvImgToVideoInplaceNode(
            genInfo.Vae.Path,
            preprocessed,
            g.CurrentMedia.Path,
            strength: 1.0,
            bypass: false);
        g.CurrentMedia = g.CurrentMedia.WithPath(
            [imgToVideoNode, 0],
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat);
        return this;
    }

    public LtxConditioningPipeline WithLtxvConditioning()
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        LTXVConditioningNode cond = bridge.AddNode(new LTXVConditioningNode());
        if (genInfo.VideoFPS.HasValue)
        {
            cond.FrameRate.Set(genInfo.VideoFPS.Value);
        }
        cond.ConnectConditioning(bridge, genInfo);

        genInfo.SetConditioning(cond);
        return this;
    }

    public LtxConditioningPipeline WithGuideAdditions(IReadOnlyList<ResolvedClipRef> clipRefs)
    {
        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (UseLtxvInplaceForRef(clipRef.Reference) || clipRef.Strength <= 0)
            {
                continue;
            }

            JArray preprocessed = ResolvePreprocessedGuidePath(clipRef.Image.Path, g.CurrentMedia);
            int frameIdx = ComputeLtxvAddGuideFrameIndex(clipRef.Reference);

            using WorkflowBridge bridge = BridgeSync.For(g);
            LTXVAddGuideNode addGuide = bridge.AddNode(new LTXVAddGuideNode()).With(
                FrameIdx: frameIdx,
                Strength: clipRef.Strength);
            addGuide.ConnectConditioning(bridge, genInfo);
            addGuide.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
            addGuide.LatentInput.ConnectFromPath(bridge, g.CurrentMedia.Path);
            addGuide.Image.ConnectFromPath(bridge, preprocessed);

            stageFrame.NeedsCropGuidesAfterSampler = true;
            genInfo.SetConditioning(addGuide);
            g.CurrentMedia = g.CurrentMedia.WithPath(
                addGuide.Latent,
                WGNodeData.DT_LATENT_VIDEO,
                genInfo.Model.Compat);
        }

        return this;
    }

    private WGNodeData ApplyLatentUpscale(string method, double scaleBy, int width, int height)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        LatentUpscaleByNode upscale = bridge.AddNode(new LatentUpscaleByNode().With(
            UpscaleMethod: method,
            ScaleBy: scaleBy));
        upscale.Samples.ConnectFromPath(bridge, stageLatent.Path);

        WGNodeData upscaled = stageLatent.WithPath(upscale.LATENT, WGNodeData.DT_LATENT_VIDEO);
        upscaled.Width = width;
        upscaled.Height = height;
        return upscaled;
    }

    private WGNodeData ApplyLatentModelUpscale(string modelName, int width, int height)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        LatentUpscaleModelLoaderNode loader = bridge.AddNode(new LatentUpscaleModelLoaderNode()).With(
            ModelName: modelName);

        LTXVLatentUpsamplerNode upsampler = bridge.AddNode(new LTXVLatentUpsamplerNode().With(
            UpscaleModel: loader.LATENTUPSCALEMODEL));
        upsampler.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
        upsampler.Samples.ConnectFromPath(bridge, stageLatent.Path);

        WGNodeData upscaled = stageLatent.WithPath(upsampler.LATENT, WGNodeData.DT_LATENT_VIDEO);
        upscaled.Width = width;
        upscaled.Height = height;
        return upscaled;
    }

    private string CreateLtxvImgToVideoInplaceNode(
        JToken vaePath,
        JArray preprocessedImagePath,
        JArray latentPath,
        double strength,
        bool bypass)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        LTXVImgToVideoInplaceNode node = bridge.AddNode(new LTXVImgToVideoInplaceNode().With(
            Strength: strength,
            Bypass: bypass));
        if (vaePath is JArray vaeArr)
        {
            node.Vae.ConnectFromPath(bridge, vaeArr);
        }
        node.Image.ConnectFromPath(bridge, preprocessedImagePath);
        node.LatentInput.ConnectFromPath(bridge, latentPath);
        return node.Id;
    }

    private JArray ResolvePreprocessedGuidePath(
        JArray guideImagePath,
        WGNodeData targetMedia) =>
        guidePreprocessReuse.ResolvePreprocessedGuidePath(guideImagePath, targetMedia);

    private static bool UseLtxvInplaceForRef(ImageReferencePlan reference) =>
        reference.FrameOrigin == ImageReferenceFrameOrigin.Start
        && reference.Frame == 1;

    private static int ComputeLtxvAddGuideFrameIndex(ImageReferencePlan reference) =>
        reference.FrameOrigin == ImageReferenceFrameOrigin.End
            ? -Math.Max(1, reference.Frame)
            : Math.Max(1, reference.Frame);
}
