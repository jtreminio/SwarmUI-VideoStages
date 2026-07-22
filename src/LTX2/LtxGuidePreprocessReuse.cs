using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal sealed class LtxGuidePreprocessReuse(
    WorkflowGenerator g,
    RootVideoStageResizer rootVideoStageResizer)
{
    private const int ImgCompression = 25;

    internal JArray ResolvePreprocessedGuidePath(JArray guideImagePath, WGNodeData targetMedia)
    {
        JArray scaledGuidePath = EnsureClipResolutionBeforeLtxvPreprocess(guideImagePath, targetMedia);
        if (TryFindReusablePreprocessOutput(scaledGuidePath, out JArray reusedPath))
        {
            return reusedPath;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        LTXVPreprocessNode preprocess = bridge.AddNode(new LTXVPreprocessNode().With(
            ImgCompression: ImgCompression));
        preprocess.Image.TryConnectFromPath(bridge, scaledGuidePath);

        return preprocess.OutputImage.ToPath();
    }

    private JArray EnsureClipResolutionBeforeLtxvPreprocess(JArray guideImagePath, WGNodeData targetMedia)
    {
        if (guideImagePath is not { Count: 2 })
        {
            return guideImagePath;
        }

        int targetW = Math.Max(16, targetMedia?.Width ?? 0);
        int targetH = Math.Max(16, targetMedia?.Height ?? 0);
        if (targetMedia?.Width is null
            || targetMedia.Height is null)
        {
            if (!rootVideoStageResizer.TryGetRootStageResolution(out targetW, out targetH))
            {
                targetW = Math.Max(16, g.UserInput.GetImageWidth());
                targetH = Math.Max(16, g.UserInput.GetImageHeight());
            }
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        if (TryGetExistingScaleAtTargetDimensions(
                bridge,
                guideImagePath,
                targetW,
                targetH,
                out ImageScaleNode existing))
        {
            existing.Crop.Set("center");
            bridge.SyncNode(existing);
            return guideImagePath;
        }

        JArray scaleSourcePath = ResolveImageScaleBaseSource(bridge, guideImagePath);
        if (ImageScaleReuse.TryFind(bridge, scaleSourcePath, targetW, targetH, out ImageScaleNode reusable))
        {
            reusable.Crop.Set("center");
            bridge.SyncNode(reusable);
            return reusable.IMAGE.ToPath();
        }

        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode().With(
            Width: targetW,
            Height: targetH,
            UpscaleMethod: "lanczos",
            Crop: "center"));
        scale.Image.TryConnectFromPath(bridge, scaleSourcePath);

        return scale.IMAGE.ToPath();
    }

    private static bool TryGetExistingScaleAtTargetDimensions(
        WorkflowBridge bridge,
        JArray imagePath,
        int targetW,
        int targetH,
        out ImageScaleNode scale)
    {
        scale = bridge.NodeAt<ImageScaleNode>(imagePath);
        return scale is not null
            && scale.Width.LiteralAsInt() == targetW
            && scale.Height.LiteralAsInt() == targetH;
    }

    private static JArray ResolveImageScaleBaseSource(WorkflowBridge bridge, JArray imagePath)
    {
        if (NodeRef.From(imagePath) is not { } start)
        {
            return imagePath;
        }

        ComfyNode current = bridge.Graph.GetNode(start.NodeId);
        int currentSlot = start.SlotIndex;
        HashSet<string> visited = [];
        while (current is ImageScaleNode scale && visited.Add($"{scale.Id}::{currentSlot}"))
        {
            INodeOutput upstream = scale.Image.Connection;
            if (upstream is null)
            {
                break;
            }
            current = upstream.Node;
            currentSlot = upstream.SlotIndex;
        }
        return current is null ? imagePath : new NodeRef(current.Id, currentSlot).ToJArray();
    }

    private bool TryFindReusablePreprocessOutput(JArray guideImagePath, out JArray preprocessOutputPath)
    {
        preprocessOutputPath = null;
        if (guideImagePath is not { Count: 2 })
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (TryResolveReusablePreprocessNode(bridge, guideImagePath, out string preprocessNodeId))
        {
            preprocessOutputPath = new JArray(preprocessNodeId, 0);
            return true;
        }

        INodeOutput startOutput = bridge.ResolvePath(guideImagePath);
        if (startOutput is null)
        {
            return false;
        }

        Queue<INodeOutput> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(startOutput);
        while (pending.Count > 0)
        {
            INodeOutput currentOutput = pending.Dequeue();
            string outputKey = $"{currentOutput.Node.Id}::{currentOutput.SlotIndex}";
            if (!visited.Add(outputKey))
            {
                continue;
            }

            foreach ((ComfyNode consumer, INodeInput input) in bridge.Graph.FindInputsConnectedTo(currentOutput))
            {
                if (input.Name != "image")
                {
                    continue;
                }

                if (consumer is LTXVPreprocessNode preprocess && HasMatchingImgCompression(preprocess))
                {
                    preprocessOutputPath = preprocess.OutputImage.ToPath();
                    return true;
                }

                if (consumer is ImageScaleNode && consumer.Outputs.Count > 0)
                {
                    pending.Enqueue(consumer.Outputs[0]);
                }
            }
        }

        return false;
    }

    private static bool TryResolveReusablePreprocessNode(
        WorkflowBridge bridge,
        JArray imagePath,
        out string preprocessNodeId)
    {
        preprocessNodeId = $"{imagePath[0]}";
        if ((int)imagePath[1] != 0)
        {
            return false;
        }
        return bridge.Graph.GetNode<LTXVPreprocessNode>(preprocessNodeId) is LTXVPreprocessNode preprocess
            && HasMatchingImgCompression(preprocess);
    }

    private static bool HasMatchingImgCompression(LTXVPreprocessNode preprocess) =>
        preprocess.ImgCompression.LiteralAsInt() == ImgCompression;
}
