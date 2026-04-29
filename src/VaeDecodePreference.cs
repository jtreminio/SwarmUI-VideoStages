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
        if (g is null || vae is null)
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
            string separated = g.CreateNode(LtxNodeTypes.LTXVSeparateAVLatent, new JObject()
            {
                ["av_latent"] = media.Path
            });
            videoRoute = new JArray(separated, 0);
            audioRoute = new JArray(separated, 1);
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

        bool useTiled = ShouldUseTiledVaeDecode(g);
        string decodeType = useTiled ? NodeTypes.VAEDecodeTiled : NodeTypes.VAEDecode;
        string decoded = g.CreateNode(
            decodeType,
            CreateVideoDecodeInputs(g, vae.Path, media.Path, useTiled));
        string decodedDataType = media.DataType == WGNodeData.DT_LATENT_VIDEO
            ? WGNodeData.DT_VIDEO
            : WGNodeData.DT_IMAGE;
        return media.WithPath(new JArray(decoded, 0), decodedDataType, vae.Compat);
    }

    private static bool ShouldUseTiledVaeDecode(WorkflowGenerator g)
    {
        return g.UserInput.TryGet(T2IParamTypes.VAETileSize, out _) == true;
    }

    private static JObject CreateVideoDecodeInputs(
        WorkflowGenerator g,
        JArray vaeRef,
        JArray latentRef,
        bool useTiled)
    {
        if (useTiled)
        {
            return new JObject()
            {
                ["vae"] = new JArray(vaeRef[0], vaeRef[1]),
                ["samples"] = new JArray(latentRef[0], latentRef[1]),
                ["tile_size"] = g.UserInput.Get(T2IParamTypes.VAETileSize, 256),
                ["overlap"] = g.UserInput.Get(T2IParamTypes.VAETileOverlap, 64),
                ["temporal_size"] = g.UserInput.Get(T2IParamTypes.VAETemporalTileSize, 32),
                ["temporal_overlap"] = g.UserInput.Get(T2IParamTypes.VAETemporalTileOverlap, 4)
            };
        }

        return new JObject()
        {
            ["vae"] = new JArray(vaeRef[0], vaeRef[1]),
            ["samples"] = new JArray(latentRef[0], latentRef[1])
        };
    }
}
