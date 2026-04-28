using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal static class StageGuideMediaHelper
{
    internal static WGNodeData ResolveGuideMedia(
        WorkflowGenerator g,
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
        if (guideReference.Media.Path is JArray guidePath
            && WorkflowUtils.TryResolveNearestDownstreamDecodeOutput(
                g.Workflow, guidePath, out JArray decodedGuidePath))
        {
            string rawDataType =
                guideReference.Media.DataType == WGNodeData.DT_LATENT_VIDEO
                || guideReference.Media.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO
                    ? WGNodeData.DT_VIDEO
                    : WGNodeData.DT_IMAGE;
            return guideReference.Media.WithPath(decodedGuidePath, rawDataType, guideVae?.Compat);
        }
        return VaeDecodePreference.AsRawImage(g, guideReference.Media, guideVae);
    }

    internal static bool IsLiveCurrentOutputReference(
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

    internal static WGNodeData PrepareGuideMedia(
        WorkflowGenerator g,
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
        if (currentWidth != targetWidth || currentHeight != targetHeight)
        {
            if (TryFindReusableImageScale(
                    g.Workflow,
                    resolvedGuideMedia.Path,
                    targetWidth,
                    targetHeight,
                    out JArray reusableScalePath))
            {
                resolvedGuideMedia = resolvedGuideMedia.WithPath(reusableScalePath);
            }
            else
            {
                JObject scaleInputs = BuildCenterLanczosImageScaleInputs(
                    resolvedGuideMedia.Path,
                    targetWidth,
                    targetHeight);
                string scaleNode = g.CreateNode(
                    NodeTypes.ImageScale,
                    scaleInputs);
                resolvedGuideMedia = resolvedGuideMedia.WithPath([scaleNode, 0]);
            }
        }

        resolvedGuideMedia.Width = targetWidth;
        resolvedGuideMedia.Height = targetHeight;
        return resolvedGuideMedia;
    }

    internal static JObject BuildCenterLanczosImageScaleInputs(JToken image, int width, int height)
    {
        return new JObject()
        {
            ["image"] = image,
            ["width"] = width,
            ["height"] = height,
            ["upscale_method"] = "lanczos",
            ["crop"] = "center"
        };
    }

    private static bool TryFindReusableImageScale(
        JObject workflow,
        JArray sourcePath,
        int targetWidth,
        int targetHeight,
        out JArray scaledPath)
    {
        scaledPath = null;
        if (workflow is null || sourcePath is not { Count: 2 })
        {
            return false;
        }

        foreach (JProperty property in workflow.Properties())
        {
            if (property.Value is not JObject node
                || !StringUtils.NodeTypeMatches(node, NodeTypes.ImageScale)
                || node["inputs"] is not JObject inputs
                || inputs["image"] is not JArray imagePath
                || imagePath.Count != 2
                || !JToken.DeepEquals(imagePath, sourcePath)
                || inputs.Value<int?>("width") != targetWidth
                || inputs.Value<int?>("height") != targetHeight)
            {
                continue;
            }

            inputs["crop"] = "center";
            scaledPath = [property.Name, 0];
            return true;
        }
        return false;
    }
}
