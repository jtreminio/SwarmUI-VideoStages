using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2;

internal static class VaeDecodePreference
{
    public static WGNodeData AsRawImage(WorkflowGenerator g, WGNodeData media, WGNodeData vae)
    {
        if (media is null)
        {
            return null;
        }
        if (media.DataType == WGNodeData.DT_IMAGE || media.DataType == WGNodeData.DT_VIDEO)
        {
            return media;
        }
        if (vae is null)
        {
            return media.AsRawImage(vae);
        }
        if (media.DataType == WGNodeData.DT_LATENT_IMAGE || media.DataType == WGNodeData.DT_LATENT_VIDEO)
        {
            return DecodeImageOrVideoLatents(g, media, vae);
        }
        if (media.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO
            && media.IsCompat(T2IModelClassSorter.CompatLtxv2))
        {
            return DecodeLtxAudioVideoLatents(g, media, vae);
        }
        return media.AsRawImage(vae);
    }

    private static WGNodeData DecodeLtxAudioVideoLatents(WorkflowGenerator g, WGNodeData media, WGNodeData vae)
    {
        (string sourceType, JObject sourceInputs) = media.SourceNodeData;
        JArray videoRoute;
        JArray audioRoute;
        if (sourceType == LTXVConcatAVLatentNode.ClassType
            && sourceInputs?["video_latent"] is JArray existingVideoRoute
            && sourceInputs["audio_latent"] is JArray existingAudioRoute)
        {
            videoRoute = existingVideoRoute;
            audioRoute = existingAudioRoute;
        }
        else
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            LTXVSeparateAVLatentNode separate = bridge.AddNode(new LTXVSeparateAVLatentNode());
            if (media.Path is JArray mediaPath)
            {
                separate.AvLatent.ConnectFromPath(bridge, mediaPath);
            }
            videoRoute = separate.VideoLatent.ToPath();
            audioRoute = separate.AudioLatent.ToPath();
        }

        WGNodeData latentVideo = media.WithPath(videoRoute, WGNodeData.DT_LATENT_VIDEO);
        latentVideo.AttachedAudio = media.WithPath(audioRoute, WGNodeData.DT_LATENT_AUDIO);
        return DecodeImageOrVideoLatents(g, latentVideo, vae);
    }

    private static WGNodeData DecodeImageOrVideoLatents(WorkflowGenerator g, WGNodeData media, WGNodeData vae)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.NodeAt(media.Path) is IVaeEncode encode
            && encode.Vae.Connection is INodeOutput encodeVae
            && bridge.ResolvePath(vae.Path) is INodeOutput targetVae
            && encodeVae.Node.Id == targetVae.Node.Id
            && encode.Pixels.Connection is INodeOutput pixels)
        {
            string rawDataType = media.DataType == WGNodeData.DT_LATENT_IMAGE
                ? WGNodeData.DT_IMAGE
                : WGNodeData.DT_VIDEO;
            return media.WithPath(NodeRef.Of(pixels).ToJArray(), rawDataType);
        }

        string decodedId = AddVaeDecode(g, vae.Path, media.Path, LtxDecodeConfig.From(g));
        string decodedDataType = media.DataType == WGNodeData.DT_LATENT_VIDEO
            ? WGNodeData.DT_VIDEO
            : WGNodeData.DT_IMAGE;
        return media.WithPath(new JArray(decodedId, 0), decodedDataType, vae.Compat);
    }

    /// <summary>Tiling geometry comes from <see cref="LtxDecodeConfig"/> so this path and the
    /// spliced post-chain rebuild always agree.</summary>
    private static string AddVaeDecode(
        WorkflowGenerator g,
        JArray vaePath,
        JArray latentPath,
        LtxDecodeConfig config)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        if (!config.UseTiledDecode)
        {
            VAEDecodeNode plain = bridge.AddNode(new VAEDecodeNode());
            plain.Vae.ConnectFromPath(bridge, vaePath);
            plain.Samples.ConnectFromPath(bridge, latentPath);
            return plain.Id;
        }
        VAEDecodeTiledNode decode = bridge.AddNode(new VAEDecodeTiledNode().With(
            TileSize: config.TileSize,
            Overlap: config.Overlap,
            TemporalSize: config.TemporalSize,
            TemporalOverlap: config.TemporalOverlap));
        decode.Vae.ConnectFromPath(bridge, vaePath);
        decode.Samples.ConnectFromPath(bridge, latentPath);
        return decode.Id;
    }
}
