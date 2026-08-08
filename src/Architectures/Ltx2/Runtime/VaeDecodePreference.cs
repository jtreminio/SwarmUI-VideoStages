using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Ltx2.Runtime.Chain;

namespace VideoStages.Architectures.Ltx2.Runtime;

internal static class VaeDecodePreference
{
    public static WGNodeData AsRawImage(
        WorkflowGenerator g,
        WGNodeData media,
        WGNodeData vae,
        string decodeId = null)
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
            return DecodeImageOrVideoLatents(g, media, vae, decodeId);
        }
        if (media.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO
            && media.IsCompat(T2IModelClassSorter.CompatLtxv2))
        {
            return DecodeLtxAudioVideoLatents(g, media, vae, decodeId);
        }
        // Audio-only, or a joint latent from another family: no LTX decode to place, so a caller
        // that claimed a host decode id does not get to spend it here. Nothing a stage produces
        // after sampling reaches this, and an unspent claim only leaves core's decode to be swept.
        return media.AsRawImage(vae);
    }

    private static WGNodeData DecodeLtxAudioVideoLatents(
        WorkflowGenerator g,
        WGNodeData media,
        WGNodeData vae,
        string decodeId)
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
        return DecodeImageOrVideoLatents(g, latentVideo, vae, decodeId);
    }

    private static WGNodeData DecodeImageOrVideoLatents(
        WorkflowGenerator g,
        WGNodeData media,
        WGNodeData vae,
        string decodeId)
    {
        // Counted, because AddDecode mints an id through this bridge: a counter-less one takes
        // max(workflow id) + 1 without moving g.LastID, which is the id the next g.CreateNode
        // then writes over in silence.
        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput targetVae = bridge.ResolvePath(vae.Path);
        INodeOutput latent = bridge.ResolvePath(media.Path);
        if (targetVae is null || latent is null)
        {
            return media;
        }
        if (bridge.NodeAt(media.Path) is IVaeEncode encode
            && encode.Vae.Connection is INodeOutput encodeVae
            && encodeVae.Node.Id == targetVae.Node.Id
            && encode.Pixels.Connection is INodeOutput pixels)
        {
            string rawDataType = media.DataType == WGNodeData.DT_LATENT_IMAGE
                ? WGNodeData.DT_IMAGE
                : WGNodeData.DT_VIDEO;
            return media.WithPath(NodeRef.Of(pixels).ToJArray(), rawDataType);
        }

        ComfyNode decode = LtxPostChainRebuilder.AddDecode(
            bridge,
            targetVae,
            latent,
            LtxDecodeConfig.From(g),
            decodeId);
        string decodedDataType = media.DataType == WGNodeData.DT_LATENT_VIDEO
            ? WGNodeData.DT_VIDEO
            : WGNodeData.DT_IMAGE;
        return media.WithPath(decode.Outputs[0], decodedDataType, vae.Compat);
    }
}
