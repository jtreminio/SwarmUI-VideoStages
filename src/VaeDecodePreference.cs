using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.LTX2;

namespace VideoStages;

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
        if (sourceType == LtxNodeTypes.LTXVConcatAVLatent
            && sourceInputs?["video_latent"] is JArray existingVideoRoute
            && sourceInputs["audio_latent"] is JArray existingAudioRoute)
        {
            videoRoute = existingVideoRoute;
            audioRoute = existingAudioRoute;
        }
        else
        {
            WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            LTXVSeparateAVLatentNode separate = bridge.AddNode(new LTXVSeparateAVLatentNode());
            if (media.Path is JArray mediaPath && bridge.ResolvePath(mediaPath) is INodeOutput avLatent)
            {
                separate.AvLatent.ConnectToUntyped(avLatent);
            }
            bridge.SyncNode(separate);
            BridgeSync.SyncLastId(g);
            videoRoute = new JArray(separate.Id, 0);
            audioRoute = new JArray(separate.Id, 1);
        }

        WGNodeData latentVideo = media.WithPath(videoRoute, WGNodeData.DT_LATENT_VIDEO);
        latentVideo.AttachedAudio = media.WithPath(audioRoute, WGNodeData.DT_LATENT_AUDIO);
        return DecodeImageOrVideoLatents(g, latentVideo, vae);
    }

    private static WGNodeData DecodeImageOrVideoLatents(WorkflowGenerator g, WGNodeData media, WGNodeData vae)
    {
        (string sourceType, JObject sourceInputs) = media.SourceNodeData;
        if ((sourceType == NodeTypes.VAEEncode || sourceType == NodeTypes.VAEEncodeTiled)
            && sourceInputs?["vae"] is JArray encodeVaePath
            && vae.Path is JArray vaePath
            && encodeVaePath.Count > 0
            && vaePath.Count > 0
            && $"{encodeVaePath[0]}" == $"{vaePath[0]}"
            && sourceInputs["pixels"] is JArray pixelsPath)
        {
            string rawDataType = media.DataType == WGNodeData.DT_LATENT_IMAGE
                ? WGNodeData.DT_IMAGE
                : WGNodeData.DT_VIDEO;
            return media.WithPath(pixelsPath, rawDataType);
        }

        string decodedId = ShouldUseTiledVaeDecode(g)
            ? AddTiledVaeDecode(g, vae.Path, media.Path)
            : AddPlainVaeDecode(g, vae.Path, media.Path);
        string decodedDataType = media.DataType == WGNodeData.DT_LATENT_VIDEO
            ? WGNodeData.DT_VIDEO
            : WGNodeData.DT_IMAGE;
        return media.WithPath(new JArray(decodedId, 0), decodedDataType, vae.Compat);
    }

    private static bool ShouldUseTiledVaeDecode(WorkflowGenerator g)
    {
        return g.UserInput.TryGet(T2IParamTypes.VAETileSize, out _);
    }

    private static string AddPlainVaeDecode(WorkflowGenerator g, JArray vaePath, JArray latentPath)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());
        if (vaePath is { Count: 2 } && bridge.ResolvePath(vaePath) is INodeOutput vae)
        {
            decode.Vae.ConnectToUntyped(vae);
        }
        if (latentPath is { Count: 2 } && bridge.ResolvePath(latentPath) is INodeOutput samples)
        {
            decode.Samples.ConnectToUntyped(samples);
        }
        bridge.SyncNode(decode);
        BridgeSync.SyncLastId(g);
        return decode.Id;
    }

    private static string AddTiledVaeDecode(WorkflowGenerator g, JArray vaePath, JArray latentPath)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        VAEDecodeTiledNode decode = bridge.AddNode(new VAEDecodeTiledNode());
        if (vaePath is { Count: 2 } && bridge.ResolvePath(vaePath) is INodeOutput vae)
        {
            decode.Vae.ConnectToUntyped(vae);
        }
        if (latentPath is { Count: 2 } && bridge.ResolvePath(latentPath) is INodeOutput samples)
        {
            decode.Samples.ConnectToUntyped(samples);
        }
        decode.TileSize.Set(g.UserInput.Get(T2IParamTypes.VAETileSize, 256));
        decode.Overlap.Set(g.UserInput.Get(T2IParamTypes.VAETileOverlap, 64));
        decode.TemporalSize.Set(g.UserInput.Get(T2IParamTypes.VAETemporalTileSize, 32));
        decode.TemporalOverlap.Set(g.UserInput.Get(T2IParamTypes.VAETemporalTileOverlap, 4));
        bridge.SyncNode(decode);
        BridgeSync.SyncLastId(g);
        return decode.Id;
    }
}
