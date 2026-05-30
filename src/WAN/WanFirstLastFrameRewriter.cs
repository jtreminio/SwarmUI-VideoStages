using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages.WAN;

internal static class WanFirstLastFrameRewriter
{
    internal static void TryRewriteToFirstLast(
        WorkflowGenerator g,
        IReadOnlyList<ImageRefSpec> refs,
        StageSpec stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData wanEndImagePrepared)
    {
        if (!VideoStageModelCompat.SupportsWanFirstLastFrame(genInfo.VideoModel)
            || stage.ClipStageIndex != 0
            || refs.Count < 2
            || wanEndImagePrepared is null)
        {
            return;
        }

        if (genInfo.PosCond is null || genInfo.PosCond.Count < 1)
        {
            Logs.Warning("VideoStages: WAN FLF rewrite skipped because conditioning output path was missing.");
            return;
        }

        using SyncingWorkflowBridge bridge = BridgeSync.For(g);
        if (bridge.NodeAt<WanImageToVideoNode>(genInfo.PosCond) is not WanImageToVideoNode wan)
        {
            return;
        }

        int width = Math.Max(16, wan.Width.LiteralAsInt() ?? g.UserInput.GetImageWidth());
        int height = Math.Max(16, wan.Height.LiteralAsInt() ?? g.UserInput.GetImageHeight());
        int length = genInfo.Frames ?? Math.Max(16, wan.Length.LiteralAsInt() ?? 49);
        int batchSize = Math.Max(1, wan.BatchSize.LiteralAsInt() ?? 1);

        INodeOutput scaledEndOutput = ResolveScaledEndOutput(
            bridge,
            wan.StartImage.Connection,
            wanEndImagePrepared.Path as JArray,
            width,
            height);
        if (scaledEndOutput is null)
        {
            Logs.Warning("VideoStages: WAN FLF rewrite skipped because the end-image output could not be resolved.");
            return;
        }

        WanFirstLastFrameToVideoNode flf = bridge.AddNode(new WanFirstLastFrameToVideoNode());
        flf.With(
            Width: width,
            Height: height,
            Length: length,
            BatchSize: batchSize);

        flf.ConnectConditioningSameAs(wan);
        flf.Vae.TryConnectSameAs(wan.Vae);
        flf.StartImage.TryConnectSameAs(wan.StartImage);
        flf.EndImage.TryConnectToUntyped(scaledEndOutput);

        if (wan.ClipVisionOutput.Connection is INodeOutput clipVisionStart)
        {
            if (clipVisionStart.Node is not CLIPVisionEncodeNode encodeStart)
            {
                Logs.Warning("VideoStages: WAN FLF rewrite skipped because CLIP vision output wiring was unexpected.");
                bridge.RemoveNode(flf);
                return;
            }

            CLIPVisionEncodeNode encodeEnd = bridge.AddNode(new CLIPVisionEncodeNode());
            encodeEnd.ClipVision.TryConnectSameAs(encodeStart.ClipVision);
            encodeEnd.Image.TryConnectToUntyped(scaledEndOutput);
            encodeEnd.With(
                Crop: "center");
            bridge.SyncNode(encodeEnd);

            flf.ClipVisionStartImage.ConnectSameAs(wan.ClipVisionOutput);
            flf.ClipVisionEndImage.ConnectTo(encodeEnd.CLIPVISIONOUTPUT);
        }

        bridge.SyncNode(flf);
        bridge.RemoveNode(wan);

        genInfo.SetConditioning(flf);
        g.CurrentMedia = g.CurrentMedia.WithPath(
            flf.Latent,
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat);
    }

    private static INodeOutput ResolveScaledEndOutput(
        WorkflowBridge bridge,
        INodeOutput startImageOutput,
        JArray endImageRawPath,
        int width,
        int height)
    {
        INodeOutput endRawOutput = endImageRawPath is { Count: 2 }
            ? bridge.ResolvePath(endImageRawPath)
            : null;
        if (endRawOutput is null)
        {
            return null;
        }

        if (startImageOutput is not null
            && CanReuseStartImageOutput(startImageOutput, endRawOutput, width, height))
        {
            return startImageOutput;
        }

        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode().With(
            Width: width,
            Height: height,
            UpscaleMethod: "lanczos",
            Crop: "disabled"));
        scale.Image.ConnectToUntyped(endRawOutput);
        bridge.SyncNode(scale);
        return scale.IMAGE;
    }

    private static bool CanReuseStartImageOutput(
        INodeOutput startImageOutput,
        INodeOutput endRawOutput,
        int width,
        int height)
    {
        if (startImageOutput.Node.Id == endRawOutput.Node.Id
            && startImageOutput.SlotIndex == endRawOutput.SlotIndex)
        {
            return true;
        }

        if (startImageOutput.Node is not ImageScaleNode startScale)
        {
            return false;
        }

        INodeOutput scaleSource = startScale.Image.Connection;
        return scaleSource is not null
            && scaleSource.Node.Id == endRawOutput.Node.Id
            && scaleSource.SlotIndex == endRawOutput.SlotIndex
            && startScale.Width.LiteralAsInt() == width
            && startScale.Height.LiteralAsInt() == height;
    }
}
