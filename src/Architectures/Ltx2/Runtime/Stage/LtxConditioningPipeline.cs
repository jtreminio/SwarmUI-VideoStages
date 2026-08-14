using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;
using VideoStages.Architectures.Ltx2.Runtime.Guide;

namespace VideoStages.Architectures.Ltx2.Runtime.Stage;

internal sealed class LtxConditioningPipeline(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageContext stageContext,
        LtxGuidePreprocessReuse guidePreprocessReuse,
        WGNodeData stageLatent)
{
    public void UpscaleLatentIfPlanned(WGNodeData sourceMedia)
    {
        StagePlan stage = stageContext.Stage;
        StageUpscalePlan upscale = stage.Core.Upscale;
        if (upscale.Mode is not (StageUpscaleMode.LatentModel or StageUpscaleMode.Latent))
        {
            return;
        }

        int baseWidth = Math.Max(sourceMedia?.Width ?? g.UserInput.GetImageWidth(), 16);
        int baseHeight = Math.Max(sourceMedia?.Height ?? g.UserInput.GetImageHeight(), 16);
        (int width, int height) =
            StageDimensionRules.ResolveUpscaled(stage, baseWidth, baseHeight);

        stageLatent = upscale.Mode == StageUpscaleMode.Latent
            ? ApplyLatentUpscale(upscale.MethodName, upscale.Factor, width, height)
            : ApplyLatentModelUpscale(upscale.MethodName, width, height);
        stageContext.ClipContext.Dimensions.Width = width;
        stageContext.ClipContext.Dimensions.Height = height;
        stageContext.ClipContext.Dimensions.HasLatentUpscale = true;
    }

    // In-place references affect only their own latent frames, preserving the rest of a retake mask.
    public void MergeOpeningFrameGuides(IReadOnlyList<ResolvedFrameRef> frameRefs)
    {
        foreach (ResolvedFrameRef frameRef in frameRefs)
        {
            if (!frameRef.Reference.IsOpeningFrame || frameRef.Strength <= 0)
            {
                continue;
            }

            stageLatent = MergeGuideIntoLatent(
                stageLatent,
                frameRef.Image.Path,
                frameRef.Strength);
        }
    }

    public void BindToCurrentMedia(WGNodeData guideMedia, double guideMergeStrength)
    {
        g.CurrentMedia = guideMedia is null || guideMergeStrength <= 0
            ? stageLatent
            : MergeGuideIntoLatent(stageLatent, guideMedia.Path, guideMergeStrength);
    }

    public void MergeContinuityAnchor()
    {
        if (stageContext.ContinuityAnchor is null)
        {
            return;
        }

        g.CurrentMedia = MergeGuideIntoLatent(
            g.CurrentMedia,
            stageContext.ContinuityAnchor.Path,
            strength: 1.0);
    }

    public void AddLtxvConditioning()
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        LTXVConditioningNode cond = bridge.AddNode(new LTXVConditioningNode());
        if (genInfo.VideoFPS.HasValue)
        {
            cond.FrameRate.Set(genInfo.VideoFPS.Value);
        }
        cond.ConnectConditioning(bridge, genInfo);

        genInfo.SetConditioning(cond);
    }

    public void AddGuideConditioning(IReadOnlyList<ResolvedFrameRef> frameRefs)
    {
        foreach (ResolvedFrameRef frameRef in frameRefs)
        {
            if (frameRef.Reference.IsOpeningFrame || frameRef.Strength <= 0)
            {
                continue;
            }

            JArray preprocessed = guidePreprocessReuse.ResolvePreprocessedGuidePath(
                frameRef.Image.Path,
                g.CurrentMedia);
            using WorkflowBridge bridge = BridgeSync.For(g);
            LTXVAddGuideNode addGuide = bridge.AddNode(new LTXVAddGuideNode()).With(
                FrameIdx: frameRef.Reference.GuideFrameIndex,
                Strength: frameRef.Strength);
            addGuide.ConnectConditioning(bridge, genInfo);
            addGuide.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
            addGuide.LatentInput.ConnectFromPath(bridge, g.CurrentMedia.Path);
            addGuide.Image.ConnectFromPath(bridge, preprocessed);

            stageContext.NeedsCropGuidesAfterSampler = true;
            genInfo.SetConditioning(addGuide);
            g.CurrentMedia = g.CurrentMedia.WithPath(
                addGuide.Latent,
                WGNodeData.DT_LATENT_VIDEO,
                genInfo.Model.Compat);
        }
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

    /// <summary>
    /// Merges a guide image into <paramref name="target"/>'s own latent frames, leaving the rest of
    /// the latent — and any retake noise mask over it — untouched.
    /// </summary>
    private WGNodeData MergeGuideIntoLatent(
        WGNodeData target,
        JArray guideImagePath,
        double strength)
    {
        JArray preprocessed =
            guidePreprocessReuse.ResolvePreprocessedGuidePath(guideImagePath, target);
        string nodeId = CreateLtxvImgToVideoInplaceNode(preprocessed, target.Path, strength);
        return target.WithPath([nodeId, 0], WGNodeData.DT_LATENT_VIDEO, genInfo.Model.Compat);
    }

    private string CreateLtxvImgToVideoInplaceNode(
        JArray preprocessedImagePath,
        JArray latentPath,
        double strength)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        LTXVImgToVideoInplaceNode node = bridge.AddNode(new LTXVImgToVideoInplaceNode().With(
            Strength: strength,
            Bypass: false));
        if (genInfo.Vae.Path is JArray vaePath)
        {
            node.Vae.ConnectFromPath(bridge, vaePath);
        }
        node.Image.ConnectFromPath(bridge, preprocessedImagePath);
        node.LatentInput.ConnectFromPath(bridge, latentPath);
        return node.Id;
    }

}
