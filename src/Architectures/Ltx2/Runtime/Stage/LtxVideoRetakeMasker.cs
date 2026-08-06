using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Authoring;
using VideoStages.Generated;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Builds the "retake" sub-graph: attaches a per-latent-frame noise mask to a VAE-encoded base-video
/// latent so only frames inside the retake window regenerate while the rest stay locked to the base
/// encoding. Built from stock graph primitives (no custom ComfyUI nodes): one 1×1 <c>SolidMask</c> per
/// frame block (0 = frozen, retake strength = window), which <see cref="LTXVSetVideoLatentNoiseMasksNode"/>
/// bilinear-resizes to the latent's H×W. Core ComfyUI applies it per step as
/// <c>x = x*mask + base*(1-mask)</c> (sampler-generic). Mirrors LTX Director's hand-rolled
/// <c>noise_mask</c> tensor, expressed as graph nodes.
/// </summary>
internal sealed class LtxVideoRetakeMasker(WorkflowGenerator g)
{
    /// <summary>
    /// LTXV VAE temporal downscale: latent frames = (pixelFrames-1)/8 + 1, pixel index → latent
    /// floor(index/8). Matches ltx_director_guide.py's downscale_index_formula[0].
    /// </summary>
    internal const int TemporalDownscale = 8;

    /// <summary>
    /// First pixel frame of latent frame <paramref name="latentIndex"/>: latent 0 holds pixel 0 alone,
    /// latent k holds pixels [8k-7, 8k]. This is the boundary grid LTXVSetAudioVideoMaskByTime
    /// searchsorts its start/end times against, so seconds snapped to it select exact latent indices.
    /// </summary>
    internal static int LatentFrameStartPixel(int latentIndex) =>
        latentIndex <= 0 ? 0 : latentIndex * TemporalDownscale - (TemporalDownscale - 1);

    private const int DefaultFrameCount = LtxStageRuntimeSettings.DefaultFrameCount;

    /// <summary>The three latent-frame block counts of a retake mask, in order.</summary>
    internal readonly record struct LatentWindow(int Prefix, int Window, int Suffix)
    {
        public int LatentLength => Prefix + Window + Suffix;
    }

    /// <summary>
    /// Converts a pixel-space retake window into Prefix/Window/Suffix latent-frame counts (frozen-lead,
    /// regenerated, frozen-tail). Clamped to the latent range; a zero-length window means nothing regenerates.
    /// </summary>
    internal static LatentWindow ComputeLatentWindow(int pixelFrames, int startFrame, int lengthFrames)
    {
        int safePixels = Math.Max(1, pixelFrames);
        int latentLength = (safePixels - 1) / TemporalDownscale + 1;

        (int startPixel, int endPixel) = RetakeWindowSpec.ClampFrameWindow(startFrame, lengthFrames, safePixels);

        int lStart = Math.Clamp(startPixel / TemporalDownscale, 0, latentLength);
        // Ceil-divide the exclusive end so any pixel frame that touches a latent frame regenerates it.
        int lEnd = Math.Clamp((endPixel + TemporalDownscale - 1) / TemporalDownscale, lStart, latentLength);

        return new LatentWindow(lStart, lEnd - lStart, latentLength - lEnd);
    }

    /// <summary>
    /// Wraps <paramref name="encodedLatent"/> with the windowed noise mask and returns the masked latent.
    /// Returns it unchanged when retake is inactive, the window is empty, or it isn't a plain video
    /// latent (audio-video is out of v1 scope).
    /// </summary>
    internal WGNodeData ApplyIfActive(
        WGNodeData encodedLatent,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        RetakePlan window,
        bool active)
    {
        if (!active
            || encodedLatent?.Path is not JArray latentPath
            || window is null
            || encodedLatent.DataType != WGNodeData.DT_LATENT_VIDEO)
        {
            return encodedLatent;
        }

        int pixelFrames = genInfo.Frames ?? encodedLatent.Frames ?? DefaultFrameCount;
        LatentWindow blocks = ComputeLatentWindow(pixelFrames, window.StartFrame, window.LengthFrames);
        if (blocks.Window <= 0)
        {
            return encodedLatent;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);

        List<NodeOutput<ImageType>> blockImages = [];
        AddBlock(bridge, blockImages, value: 0.0, count: blocks.Prefix);
        AddBlock(bridge, blockImages, value: window.Strength, count: blocks.Window);
        AddBlock(bridge, blockImages, value: 0.0, count: blocks.Suffix);

        NodeOutput<ImageType> maskImages;
        if (blockImages.Count == 1)
        {
            maskImages = blockImages[0];
        }
        else
        {
            BatchImagesNodeNode batch = bridge.AddNode(new BatchImagesNodeNode());
            foreach (NodeOutput<ImageType> image in blockImages)
            {
                batch.Images.Add(image);
            }
            maskImages = batch.IMAGE;
        }

        ImageToMaskNode toMask = bridge.AddNode(new ImageToMaskNode().With(
            Image: maskImages,
            Channel: "red"));

        LTXVSetVideoLatentNoiseMasksNode setMask = bridge.AddNode(new LTXVSetVideoLatentNoiseMasksNode().With(
            Masks: toMask.MASK));
        setMask.Samples.ConnectFromPath(bridge, latentPath);

        return encodedLatent.WithPath(setMask.LATENT.ToPath());
    }

    private void AddBlock(
        WorkflowBridge bridge,
        List<NodeOutput<ImageType>> blockImages,
        double value,
        int count)
    {
        if (count <= 0)
        {
            return;
        }
        SolidMaskNode solid = bridge.AddNode(new SolidMaskNode().With(
            Value: value,
            Width: 1,
            Height: 1));
        MaskToImageNode toImage = bridge.AddNode(new MaskToImageNode().With(
            Mask: solid.MASK));
        RepeatImageBatchNode repeat = bridge.AddNode(new RepeatImageBatchNode().With(
            Image: toImage.IMAGE,
            Amount: count));
        blockImages.Add(repeat.IMAGE);
    }
}
