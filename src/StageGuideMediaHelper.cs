using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal sealed class StageGuideMediaHelper(WorkflowGenerator g)
{
    internal WGNodeData ResolveGuideMedia(
        StageRefStore.StageRef guideReference,
        LTX2.LtxPostVideoChain postVideoChain)
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
            WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            INodeOutput guideOutput = bridge.ResolvePath(guidePath);
            ComfyNode decode = guideOutput is not null
                ? bridge.Graph.FindNearestDownstream(guideOutput, n => n is VAEDecodeNode or VAEDecodeTiledNode)
                : null;
            if (decode is not null)
            {
                string rawDataType =
                    guideReference.Media.DataType == WGNodeData.DT_LATENT_VIDEO
                    || guideReference.Media.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO
                        ? WGNodeData.DT_VIDEO
                        : WGNodeData.DT_IMAGE;
                return guideReference.Media.WithPath(new JArray(decode.Id, 0), rawDataType, guideVae?.Compat);
            }
        }
        return VaeDecodePreference.AsRawImage(g, guideReference.Media, guideVae);
    }

    internal bool IsLiveCurrentOutputReference(
        WGNodeData guideMedia,
        LTX2.LtxPostVideoChain postVideoChain)
    {
        if (guideMedia?.Path is not JArray guidePath || postVideoChain is null)
        {
            return false;
        }

        return JToken.DeepEquals(guidePath, postVideoChain.CurrentOutputMedia?.Path)
            || JToken.DeepEquals(guidePath, postVideoChain.DecodeOutputPath)
            || JToken.DeepEquals(guidePath, postVideoChain.AvLatentPath);
    }

    internal WGNodeData PrepareGuideMedia(
        WGNodeData guideMedia,
        WGNodeData sourceMedia,
        bool scaleToSourceSize)
    {
        WGNodeData resolvedGuideMedia = guideMedia ?? sourceMedia;
        if (!scaleToSourceSize)
        {
            return resolvedGuideMedia;
        }

        int targetWidth = sourceMedia.Width ?? g.UserInput.GetImageWidth();
        int targetHeight = sourceMedia.Height ?? g.UserInput.GetImageHeight();
        int currentWidth = resolvedGuideMedia.Width ?? targetWidth;
        int currentHeight = resolvedGuideMedia.Height ?? targetHeight;

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (TryNormalizeExistingImageScale(
            bridge,
            resolvedGuideMedia.Path,
            targetWidth,
            targetHeight,
            out string normalizedScaleId))
        {
            resolvedGuideMedia = resolvedGuideMedia.WithPath([normalizedScaleId, 0]);
        }
        else if (currentWidth != targetWidth || currentHeight != targetHeight)
        {
            if (TryFindReusableImageScale(
                bridge,
                resolvedGuideMedia.Path,
                targetWidth,
                targetHeight,
                out string reusableScaleId))
            {
                resolvedGuideMedia = resolvedGuideMedia.WithPath([reusableScaleId, 0]);
            }
            else
            {
                ImageScaleNode scale = CreateCenterLanczosImageScale(
                    bridge,
                    resolvedGuideMedia.Path,
                    targetWidth,
                    targetHeight);
                resolvedGuideMedia = resolvedGuideMedia.WithPath([scale.Id, 0]);
            }
        }

        resolvedGuideMedia.Width = targetWidth;
        resolvedGuideMedia.Height = targetHeight;
        return resolvedGuideMedia;
    }

    private ImageScaleNode CreateCenterLanczosImageScale(
        WorkflowBridge bridge,
        JArray sourcePath,
        int width,
        int height)
    {
        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode());
        if (sourcePath is { Count: 2 } && bridge.ResolvePath(sourcePath) is INodeOutput source)
        {
            scale.Image.ConnectToUntyped(source);
        }
        scale.Width.Set(width);
        scale.Height.Set(height);
        scale.UpscaleMethod.Set("lanczos");
        scale.Crop.Set("center");
        bridge.SyncNode(scale);
        BridgeSync.SyncLastId(g);
        return scale;
    }

    private static bool TryNormalizeExistingImageScale(
        WorkflowBridge bridge,
        JArray sourcePath,
        int targetWidth,
        int targetHeight,
        out string scaleNodeId)
    {
        scaleNodeId = null;
        if (sourcePath is not { Count: 2 }
            || bridge.Graph.GetNode($"{sourcePath[0]}") is not ImageScaleNode scale)
        {
            return false;
        }

        ImageScaleNode collapsed = scale;
        if (scale.Image.Connection?.Node is ImageScaleNode upstream)
        {
            INodeOutput outerOutput = bridge.ResolvePath(sourcePath);
            if (outerOutput is not null && !bridge.Graph.FindDownstream(outerOutput).Any())
            {
                bridge.RemoveNode(scale);
            }
            collapsed = upstream;
        }

        collapsed.Width.Set(targetWidth);
        collapsed.Height.Set(targetHeight);
        collapsed.Crop.Set("center");
        if (!collapsed.UpscaleMethod.HasValue)
        {
            collapsed.UpscaleMethod.Set("lanczos");
        }
        bridge.SyncNode(collapsed);
        scaleNodeId = collapsed.Id;
        return true;
    }

    private static bool TryFindReusableImageScale(
        WorkflowBridge bridge,
        JArray sourcePath,
        int targetWidth,
        int targetHeight,
        out string scaleNodeId)
    {
        scaleNodeId = null;
        if (sourcePath is not { Count: 2 })
        {
            return false;
        }
        string sourceId = $"{sourcePath[0]}";
        int sourceSlot = (int)sourcePath[1];

        foreach (ImageScaleNode candidate in bridge.Graph.NodesOfType<ImageScaleNode>())
        {
            INodeOutput candidateImage = candidate.Image.Connection;
            if (candidateImage?.Node.Id != sourceId
                || candidateImage.SlotIndex != sourceSlot
                || candidate.Width.LiteralAsInt() != targetWidth
                || candidate.Height.LiteralAsInt() != targetHeight)
            {
                continue;
            }
            candidate.Crop.Set("center");
            bridge.SyncNode(candidate);
            scaleNodeId = candidate.Id;
            return true;
        }
        return false;
    }
}
