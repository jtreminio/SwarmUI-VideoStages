using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Builds the optional pixel/model upscale graph that feeds a stage.</summary>
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
        // ClipDimensionState is updated by every prior stage, including latent-only upscalers.
        // A decoded WGNodeData can retain the pre-upscale marker dimensions even though its latent
        // path is already larger, so the clip state is authoritative for the next stage.
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

        // Once a clip has entered a latent-upscaled resolution, later pixel/model requests are
        // intentionally ignored. Decoding merely to resize and re-encode would discard the
        // latent-upscaler's representation and used to leave duplicate scale scaffolding.
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
        bool isLtxv2Stage = Ltx2ModelCompatibility.IsLtxV2VideoModel(stageVideoModel);
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
            WGNodeData scaled = new StagePixelScaleGraphBuilder(g).Apply(
                upscaleSource,
                targetWidth,
                targetHeight,
                upscale.MethodName);
            return PublishUpscaledMedia(
                scaled,
                dimensions,
                targetWidth,
                targetHeight);
        }

        if (upscale.Mode == StageUpscaleMode.Model)
        {
            ImageScaleNode fitScale = AddModelUpscaleChain(
                upscaleSource.Path,
                upscale.MethodName,
                targetWidth,
                targetHeight);
            return PublishUpscaledMedia(
                upscaleSource.WithPath(fitScale.IMAGE),
                dimensions,
                targetWidth,
                targetHeight);
        }

        PlanDiagnosticReporter.TrackRequestWarning(
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

    private ImageScaleNode AddModelUpscaleChain(
        JArray sourcePath,
        string modelName,
        int targetWidth,
        int targetHeight)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        UpscaleModelLoaderNode loader = bridge.AddNode(new UpscaleModelLoaderNode()).With(
            ModelName: modelName);

        ImageUpscaleWithModelNode upscale = bridge.AddNode(new ImageUpscaleWithModelNode().With(
            UpscaleModel: loader.UPSCALEMODEL));
        upscale.Image.ConnectFromPath(bridge, sourcePath);

        return bridge.AddNode(new ImageScaleNode().With(
            Width: targetWidth,
            Height: targetHeight,
            UpscaleMethod: "lanczos",
            Crop: "disabled",
            Image: upscale.IMAGE));
    }
}
