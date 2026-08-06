using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Graph;

namespace VideoStages.Execution;

/// <summary>Prepares guide media for a consumer's dimensions and framing policy.</summary>
internal static class GuideMediaPreparation
{
    internal static WGNodeData Prepare(
        WorkflowGenerator generator,
        WGNodeData guideMedia,
        WGNodeData sourceMedia,
        bool scaleToSourceSize,
        ReferenceFramingMode referenceFraming = ReferenceFramingMode.Crop)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(sourceMedia);

        WGNodeData resolvedGuideMedia = guideMedia ?? sourceMedia;
        if (!scaleToSourceSize)
        {
            return resolvedGuideMedia;
        }

        int targetWidth = sourceMedia.Width ?? generator.UserInput.GetImageWidth();
        int targetHeight = sourceMedia.Height ?? generator.UserInput.GetImageHeight();
        int currentWidth = resolvedGuideMedia.Width ?? targetWidth;
        int currentHeight = resolvedGuideMedia.Height ?? targetHeight;

        using WorkflowBridge bridge = BridgeSync.For(generator);
        if (currentWidth != targetWidth || currentHeight != targetHeight)
        {
            JArray framedPath = ReferenceFramingGraph.Frame(
                bridge,
                resolvedGuideMedia.Path,
                targetWidth,
                targetHeight,
                referenceFraming,
                unwrapExistingFraming: false);
            resolvedGuideMedia = resolvedGuideMedia.WithPath(framedPath);
        }

        resolvedGuideMedia.Width = targetWidth;
        resolvedGuideMedia.Height = targetHeight;
        return resolvedGuideMedia;
    }
}
