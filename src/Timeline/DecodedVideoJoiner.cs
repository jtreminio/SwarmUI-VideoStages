using ComfyTyped.Core;
using ComfyTyped.Generated;
using VideoStages.Execution;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages.Timeline;

/// <summary>Joins clips into one image batch, blending overlapped boundaries in pixel space.</summary>
internal static class DecodedVideoJoiner
{
    /// <summary>
    /// Concatenates clip video on the resolved boundary timeline. Overlapped boundaries trim both
    /// sides and splice in a ramp-masked blend of the frames they removed.
    /// </summary>
    internal static INodeOutput Merge(
        WorkflowBridge bridge,
        IReadOnlyList<DecodedClipArtifact> clips,
        IReadOnlyList<INodeOutput> videoOutputs,
        TimelineOverlapTrims trims)
    {
        if (trims is null)
        {
            return Concat(bridge, videoOutputs);
        }

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
            int startTrim = i > 0 ? trims.TrimFrames[i - 1] : 0;
            int endTrim = i < videoOutputs.Count - 1 ? trims.TrimFrames[i] : 0;
            int frames = clips[i].Frames;
            segments.Add(startTrim == 0 && endTrim == 0
                ? videoOutputs[i]
                : SliceImageFrames(
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

        return Concat(bridge, segments);
    }

    /// <summary>
    /// Batches every segment into one image stream, cascading through further batch nodes whenever
    /// the count exceeds what a single node's input list holds.
    /// </summary>
    private static INodeOutput Concat(WorkflowBridge bridge, IReadOnlyList<INodeOutput> outputs)
    {
        List<INodeOutput> layer = [.. outputs];
        while (true)
        {
            BatchImagesNodeNode node = bridge.AddNode(new BatchImagesNodeNode());
            int taken = Math.Min(layer.Count, node.Images.Max);
            for (int i = 0; i < taken; i++)
            {
                node.Images.AddFromUntyped(layer[i]);
            }
            if (taken == layer.Count)
            {
                return node.IMAGE;
            }
            layer = [node.IMAGE, .. layer.Skip(taken)];
        }
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
