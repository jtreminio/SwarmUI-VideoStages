using ComfyTyped.Core;
using ComfyTyped.Generated;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;

namespace VideoStages;

/// <summary>Builds the image graph for cut and overlapped multi-clip timelines.</summary>
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

    public static INodeOutput MergeWithOverlaps(
        WorkflowBridge bridge,
        IReadOnlyList<WGNodeData> clips,
        IReadOnlyList<INodeOutput> videoOutputs,
        BoundaryOverlapPlan plan)
    {
        Dictionary<int, INodeOutput> rampMasks = [];
        INodeOutput RampMaskFor(int k)
        {
            if (!rampMasks.TryGetValue(k, out INodeOutput mask))
            {
                mask = BuildCrossfadeRampMask(bridge, k, clips[0].Width.Value, clips[0].Height.Value);
                rampMasks[k] = mask;
            }
            return mask;
        }

        List<INodeOutput> segments = [];
        for (int i = 0; i < videoOutputs.Count; i++)
        {
            int startTrim = i > 0 ? plan.BoundaryOverlap[i - 1] : 0;
            int endTrim = i < videoOutputs.Count - 1 ? plan.BoundaryOverlap[i] : 0;
            int frames = clips[i].Frames.Value;
            segments.Add(SliceImageFrames(bridge, videoOutputs[i], startTrim, frames - startTrim - endTrim));

            if (endTrim > 0)
            {
                INodeOutput tail = SliceImageFrames(bridge, videoOutputs[i], frames - endTrim, endTrim);
                INodeOutput head = SliceImageFrames(bridge, videoOutputs[i + 1], 0, endTrim);
                segments.Add(AddPyramidBlend(bridge, tail, head, RampMaskFor(endTrim)));
            }
        }

        return MergeCut(bridge, segments);
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

    private static INodeOutput SliceImageFrames(WorkflowBridge bridge, INodeOutput source, int batchIndex, int length)
    {
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode()).With(BatchIndex: batchIndex, Length: length);
        node.Image.ConnectToUntyped(source);
        return node.IMAGE;
    }

    private static INodeOutput BuildCrossfadeRampMask(WorkflowBridge bridge, int frames, int width, int height)
    {
        SwarmRampMaskBatchNode ramp = bridge.AddNode(new SwarmRampMaskBatchNode().With(
            Frames: frames,
            Width: width,
            Height: height));
        return ramp.Mask;
    }

    private static INodeOutput AddPyramidBlend(
        WorkflowBridge bridge,
        INodeOutput imageA,
        INodeOutput imageB,
        INodeOutput mask)
    {
        LTXVLaplacianPyramidBlendNode blend = bridge.AddNode(new LTXVLaplacianPyramidBlendNode().With(
            TrimToShortest: true,
            MaskLowResDilation: 0));
        blend.ImageA.ConnectToUntyped(imageA);
        blend.ImageB.ConnectToUntyped(imageB);
        blend.Mask.ConnectToUntyped(mask);
        return blend.Image;
    }
}
