using ComfyTyped.Core;
using ComfyTyped.Generated;

namespace VideoStages;

/// <summary>Builds architecture-neutral hard-cut image concatenation.</summary>
internal static class MultiClipVideoGraphAssembler
{
    private const int BatchImagesNodeMaxInputs = 50;

    public static INodeOutput MergeCut(WorkflowBridge bridge, IReadOnlyList<INodeOutput> outputs)
    {
        if (outputs.Count == 1)
        {
            return outputs[0];
        }

        List<INodeOutput> layer = [.. outputs];
        while (layer.Count > BatchImagesNodeMaxInputs)
        {
            INodeOutput chunk = AddBatchImagesNode(bridge, layer.Take(BatchImagesNodeMaxInputs));
            List<INodeOutput> next = [chunk];
            for (int i = BatchImagesNodeMaxInputs; i < layer.Count; i++)
            {
                next.Add(layer[i]);
            }
            layer = next;
        }

        return AddBatchImagesNode(bridge, layer);
    }

    private static INodeOutput AddBatchImagesNode(WorkflowBridge bridge, IEnumerable<INodeOutput> imageOutputs)
    {
        BatchImagesNodeNode node = bridge.AddNode(new BatchImagesNodeNode());
        foreach (INodeOutput imageOutput in imageOutputs)
        {
            node.Images.AddFromUntyped(imageOutput);
        }
        return node.IMAGE;
    }

}
