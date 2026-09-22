using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Execution.Graph;

internal static class LtxPreprocessInputs
{
    internal static void EnsureRgb(WorkflowGenerator generator)
    {
        using WorkflowBridge bridge = BridgeSync.For(generator);
        foreach (LTXVPreprocessNode preprocess in bridge.Graph.NodesOfType<LTXVPreprocessNode>().ToArray())
        {
            // Compression uses an rgb24 encoder; bypassed preprocessing preserves alpha.
            if (preprocess.ImgCompression.LiteralAsInt() == 0
                || preprocess.Image.Connection is not INodeOutput source
                || source.Node is SplitImageWithAlphaNode)
            {
                continue;
            }
            SplitImageWithAlphaNode split = bridge.Graph.NodesOfType<SplitImageWithAlphaNode>()
                .FirstOrDefault(node => node.Image.Connection == source);
            if (split is null)
            {
                split = bridge.AddNode(new SplitImageWithAlphaNode());
                split.Image.ConnectToUntyped(source);
            }
            preprocess.Image.ConnectTo(split.IMAGE);
            VideoGraphHelpers.InvalidateForRemovedNodes(generator.NodeHelpers, [preprocess.Id]);
        }
    }
}
