using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class StageUpscaleGraphBuilder(WorkflowGenerator g)
{
    public WGNodeData Apply(
        ClipContext clipContext,
        StagePlan stage,
        int sectionId,
        LtxPostVideoChainCapture postVideoChain)
    {
        ArgumentNullException.ThrowIfNull(clipContext);
        ArgumentNullException.ThrowIfNull(stage);
        StageUpscalePlan upscale = stage.Core.Upscale;

        ClipDimensionState dimensions = clipContext.Dimensions;
        WGNodeData source = VaeDecodePreference.AsRawImage(g, g.CurrentMedia, g.CurrentVae);
        // Clip dimensions include latent-only upscales that decoded-media metadata may not.
        int width = Math.Max(dimensions.Width, 16);
        int height = Math.Max(dimensions.Height, 16);
        source.Width = width;
        source.Height = height;

        if (upscale.Mode == StageUpscaleMode.None
            || string.IsNullOrWhiteSpace(upscale.RawMethod))
        {
            g.CurrentMedia = source;
            return source;
        }

        // Preserve a latent-upscaled representation instead of decoding it for another resize.
        if (dimensions.HasLatentUpscale
            && upscale.Mode is StageUpscaleMode.Pixel or StageUpscaleMode.Model)
        {
            g.CurrentMedia = source;
            return source;
        }

        T2IModel stageVideoModel = g.UserInput.Get(
            T2IParamTypes.VideoModel,
            null,
            sectionId: sectionId);
        bool isLtxv2Stage = Ltx2ArchitectureModule.IsLtxV2VideoModel(stageVideoModel);
        if (isLtxv2Stage
            && upscale.Mode is StageUpscaleMode.LatentModel or StageUpscaleMode.Latent)
        {
            g.CurrentMedia = source;
            return source;
        }

        (int targetWidth, int targetHeight) =
            StageDimensionRules.ResolveUpscaled(stage, width, height);
        WGNodeData upscaleSource = ResolveSourceMedia(source, postVideoChain, width, height);
        if (upscale.Mode == StageUpscaleMode.Pixel)
        {
            WGNodeData scaled = new StageUpscaleGraph(g).Apply(
                upscaleSource,
                targetWidth,
                targetHeight,
                upscale.Mode,
                upscale.MethodName);
            return PublishUpscaledMedia(
                scaled,
                dimensions,
                targetWidth,
                targetHeight);
        }

        if (upscale.Mode == StageUpscaleMode.Model)
        {
            WGNodeData scaled = new StageUpscaleGraph(g).Apply(
                upscaleSource,
                targetWidth,
                targetHeight,
                upscale.Mode,
                upscale.MethodName);
            return PublishUpscaledMedia(
                scaled,
                dimensions,
                targetWidth,
                targetHeight);
        }

        RequestWarnings.Track(
            g.UserInput,
            $"VideoStages: Stage {stage.StageId} uses unsupported upscale method "
            + $"'{upscale.RawMethod}'. Ignoring upscale.");
        g.CurrentMedia = source;
        return source;
    }

    private WGNodeData PublishUpscaledMedia(
        WGNodeData media,
        ClipDimensionState dimensions,
        int width,
        int height)
    {
        g.CurrentMedia = media;
        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
        dimensions.Width = width;
        dimensions.Height = height;
        return g.CurrentMedia;
    }

    private WGNodeData ResolveSourceMedia(
        WGNodeData source,
        LtxPostVideoChainCapture postVideoChain,
        int width,
        int height)
    {
        if (postVideoChain is null
            || !postVideoChain.ReferencesOutput(source))
        {
            return source;
        }

        WGNodeData detached = postVideoChain.CreateDetachedGuideMedia(g.CurrentVae);
        if (detached is null)
        {
            return source;
        }
        detached.Width = width;
        detached.Height = height;
        return detached;
    }

}
