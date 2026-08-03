using ComfyTyped.Core;
using ComfyTyped.Generated;
using VideoStages.Execution;
using VideoStages.Generated;

namespace VideoStages;

internal static class DecodedCrossfadeAssembler
{
    internal static INodeOutput MergeOverlaps(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips,
        IReadOnlyList<INodeOutput> videoOutputs,
        BoundaryOverlapPlan plan)
    {
        Dictionary<int, INodeOutput> rampMasks = [];
        INodeOutput RampMaskFor(int frames)
        {
            if (!rampMasks.TryGetValue(frames, out INodeOutput mask))
            {
                mask = BuildCrossfadeRampMask(
                    bridge,
                    frames,
                    clips[0].Width,
                    clips[0].Height);
                rampMasks[frames] = mask;
            }
            return mask;
        }

        List<INodeOutput> segments = [];
        for (int i = 0; i < videoOutputs.Count; i++)
        {
            int startTrim = i > 0 ? plan.BoundaryOverlap[i - 1] : 0;
            int endTrim = i < videoOutputs.Count - 1 ? plan.BoundaryOverlap[i] : 0;
            int frames = clips[i].Frames;
            segments.Add(SliceImageFrames(
                bridge,
                videoOutputs[i],
                startTrim,
                frames - startTrim - endTrim));

            if (endTrim > 0)
            {
                INodeOutput tail =
                    SliceImageFrames(bridge, videoOutputs[i], frames - endTrim, endTrim);
                INodeOutput head = SliceImageFrames(bridge, videoOutputs[i + 1], 0, endTrim);
                segments.Add(AddBlend(
                    bridge,
                    tail,
                    head,
                    RampMaskFor(endTrim)));
            }
        }

        return MultiClipVideoGraphAssembler.MergeCut(bridge, segments);
    }

    private static INodeOutput SliceImageFrames(
        WorkflowBridge bridge,
        INodeOutput source,
        int batchIndex,
        int length)
    {
        ImageFromBatchNode node = bridge.AddNode(
            new ImageFromBatchNode()).With(BatchIndex: batchIndex, Length: length);
        node.Image.ConnectToUntyped(source);
        return node.IMAGE;
    }

    private static INodeOutput BuildCrossfadeRampMask(
        WorkflowBridge bridge,
        int frames,
        int width,
        int height)
    {
        SwarmRampMaskBatchNode ramp = bridge.AddNode(new SwarmRampMaskBatchNode().With(
            Frames: frames,
            Width: width,
            Height: height));
        return ramp.Mask;
    }

    private static INodeOutput AddBlend(
        WorkflowBridge bridge,
        INodeOutput tail,
        INodeOutput head,
        INodeOutput mask)
    {
        ImageCompositeMaskedNode blend = bridge.AddNode(new ImageCompositeMaskedNode());
        blend.Destination.ConnectToUntyped(head);
        blend.Source.ConnectToUntyped(tail);
        blend.Mask.ConnectToUntyped(mask);
        return blend.IMAGE;
    }
}
