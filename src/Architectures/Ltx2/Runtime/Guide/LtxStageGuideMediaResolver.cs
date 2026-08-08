using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Runtime.Chain;

namespace VideoStages.Architectures.Ltx2.Runtime.Guide;

/// <summary>Resolves LTX stage references across post-video-chain and latent boundaries.</summary>
internal sealed class LtxStageGuideMediaResolver(WorkflowGenerator g)
{
    internal WGNodeData ResolveGuideMedia(
        StageRefStore.StageRef guideReference,
        LtxPostVideoChain postVideoChain)
    {
        if (guideReference?.Media is null)
        {
            return null;
        }
        if (postVideoChain is not null
            && IsLiveCurrentOutputReference(guideReference.Media, postVideoChain))
        {
            WGNodeData detachedGuideVae = guideReference.Vae
                ?? postVideoChain.CreateStageInputVae()
                ?? g.CurrentVae;
            return postVideoChain.CreateDetachedGuideMedia(detachedGuideVae);
        }
        if (guideReference.Media.DataType == WGNodeData.DT_IMAGE
            || guideReference.Media.DataType == WGNodeData.DT_VIDEO)
        {
            return guideReference.Media;
        }

        WGNodeData guideVae = guideReference.Vae ?? g.CurrentVae;
        if (guideReference.Media.Path is JArray { Count: 2 } guidePath)
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            INodeOutput guideOutput = bridge.ResolvePath(guidePath);
            // Stop at the sampler: a decode on its far side is of different pixels, so an
            // unbounded walk resolved a Base reference to the refined image whenever core built no
            // pre-refiner decode of its own — which, under the default PostApply refiner with no
            // upscale, is always.
            IVaeDecode decode = guideOutput is not null
                ? bridge.Graph.FindNearestDownstream<IVaeDecode, SwarmKSamplerNode>(guideOutput)
                : null;
            if (decode is not null)
            {
                string rawDataType =
                    guideReference.Media.DataType == WGNodeData.DT_LATENT_VIDEO
                    || guideReference.Media.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO
                        ? WGNodeData.DT_VIDEO
                        : WGNodeData.DT_IMAGE;
                return guideReference.Media.WithPath(decode.IMAGE, rawDataType, guideVae?.Compat);
            }
        }
        return VaeDecodePreference.AsRawImage(g, guideReference.Media, guideVae);
    }

    internal bool IsLiveCurrentOutputReference(
        WGNodeData guideMedia,
        LtxPostVideoChain postVideoChain)
    {
        if (guideMedia?.Path is not JArray guidePath || postVideoChain is null)
        {
            return false;
        }

        return postVideoChain.ReferencesOutput(guideMedia)
            || JToken.DeepEquals(guidePath, postVideoChain.AvLatentPath);
    }
}
