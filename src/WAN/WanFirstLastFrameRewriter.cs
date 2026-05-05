using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Typed;

namespace VideoStages.WAN;

internal static class WanFirstLastFrameRewriter
{
    internal static void TryRewriteToFirstLast(
        WorkflowGenerator g,
        JsonParser.StageSpec stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData wanEndImagePrepared)
    {
        if (!VideoStageModelCompat.SupportsWanFirstLastFrame(genInfo.VideoModel)
            || stage.ClipStageIndex != 0
            || stage.ClipRefs is not { Count: >= 2 }
            || wanEndImagePrepared is null)
        {
            return;
        }

        if (genInfo.PosCond is null || genInfo.PosCond.Count < 1)
        {
            Logs.Warning("VideoStages: WAN FLF rewrite skipped because conditioning output path was missing.");
            return;
        }

        string wanNodeId = $"{genInfo.PosCond[0]}";
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.Graph.GetNode<WanImageToVideoNode>(wanNodeId) is not WanImageToVideoNode wan)
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

        WanFirstLastFrameToVideoNode flf = bridge.AddNode(new WanFirstLastFrameToVideoNode());
        flf.Width.Set((long)width);
        flf.Height.Set((long)height);
        flf.Length.Set((long)length);
        flf.BatchSize.Set((long)batchSize);
        if (wan.PositiveInput.Connection is INodeOutput pos) { flf.PositiveInput.ConnectToUntyped(pos); }
        if (wan.NegativeInput.Connection is INodeOutput neg) { flf.NegativeInput.ConnectToUntyped(neg); }
        if (wan.Vae.Connection is INodeOutput vae) { flf.Vae.ConnectToUntyped(vae); }
        if (wan.StartImage.Connection is INodeOutput startImg) { flf.StartImage.ConnectToUntyped(startImg); }
        if (scaledEndOutput is not null) { flf.EndImage.ConnectToUntyped(scaledEndOutput); }

        if (wan.ClipVisionOutput.Connection is INodeOutput clipVisionStart)
        {
            if (clipVisionStart.Node is not CLIPVisionEncodeNode encodeStart)
            {
                Logs.Warning("VideoStages: WAN FLF rewrite skipped because CLIP vision output wiring was unexpected.");
                bridge.RemoveNode(flf);
                return;
            }

            CLIPVisionEncodeNode encodeEnd = bridge.AddNode(new CLIPVisionEncodeNode());
            if (encodeStart.ClipVision.Connection is INodeOutput clipLoader)
            {
                encodeEnd.ClipVision.ConnectToUntyped(clipLoader);
            }
            if (scaledEndOutput is not null)
            {
                encodeEnd.Image.ConnectToUntyped(scaledEndOutput);
            }
            encodeEnd.Crop.Set("center");
            bridge.SyncNode(encodeEnd);

            flf.ClipVisionStartImage.ConnectToUntyped(clipVisionStart);
            flf.ClipVisionEndImage.ConnectTo(encodeEnd.CLIPVISIONOUTPUT);
        }

        bridge.SyncNode(flf);
        bridge.RemoveNode(wan);
        BridgeSync.SyncLastId(g);

        genInfo.PosCond = new JArray(flf.Id, 0);
        genInfo.NegCond = new JArray(flf.Id, 1);
        g.CurrentMedia = g.CurrentMedia.WithPath(
            new JArray(flf.Id, 2),
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
        INodeOutput endRawOutput = endImageRawPath is { Count: 2 } ? bridge.ResolvePath(endImageRawPath) : null;
        if (endRawOutput is null)
        {
            return null;
        }

        if (startImageOutput is not null && CanReuseStartImageOutput(startImageOutput, endRawOutput, width, height))
        {
            return startImageOutput;
        }

        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode());
        scale.Image.ConnectToUntyped(endRawOutput);
        scale.Width.Set((long)width);
        scale.Height.Set((long)height);
        scale.UpscaleMethod.Set("lanczos");
        scale.Crop.Set("disabled");
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
