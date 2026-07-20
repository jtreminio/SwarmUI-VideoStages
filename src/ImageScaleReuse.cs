using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;

namespace VideoStages;

/// <summary>Finds an existing ImageScale node so repeated scale requests over the
/// same source reuse one node instead of stacking duplicates in the graph.</summary>
internal static class ImageScaleReuse
{
    /// <summary>True when the graph already scales <paramref name="sourcePath"/> to
    /// exactly (targetW, targetH). <paramref name="mutateMatch"/>, when given, runs on
    /// the match before it is returned (e.g. force center-crop and sync).</summary>
    public static bool TryFind(
        WorkflowBridge bridge,
        JArray sourcePath,
        int targetW,
        int targetH,
        out ImageScaleNode scale,
        Action<ImageScaleNode> mutateMatch = null)
    {
        scale = null;
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
                || candidate.Width.LiteralAsInt() != targetW
                || candidate.Height.LiteralAsInt() != targetH)
            {
                continue;
            }
            mutateMatch?.Invoke(candidate);
            scale = candidate;
            return true;
        }
        return false;
    }
}
