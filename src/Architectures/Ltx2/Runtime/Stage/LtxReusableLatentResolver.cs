using ComfyTyped.Core;
using ComfyTyped.Families;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxReusableLatentResolver(WorkflowGenerator g)
{
    internal bool TryResolve(
        WGNodeData sourceMedia,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        bool allowDynamicFrameCount,
        out JArray latentPath)
    {
        latentPath = null;
        if (sourceMedia?.DataType != WGNodeData.DT_VIDEO
            || sourceMedia.Path is null
            || genInfo?.Vae?.Path is null
            || (!genInfo.Frames.HasValue && !allowDynamicFrameCount)
            || genInfo.Frames.HasValue
                && sourceMedia.Frames is int sourceFrames
                && sourceFrames > genInfo.Frames.Value)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        (INodeOutput samples, INodeOutput decodeVae) =
            bridge.ResolvePath(sourceMedia.Path)?.Node is IVaeDecode decode
                ? (decode.Samples.Connection, decode.Vae.Connection)
                : (null, null);
        if (samples is null || decodeVae is null)
        {
            return false;
        }

        INodeOutput vaeOutput = bridge.ResolvePath(genInfo.Vae.Path);
        bool sameVaeNode = vaeOutput is not null
            && decodeVae.Node == vaeOutput.Node
            && decodeVae.SlotIndex == vaeOutput.SlotIndex;
        bool sameDynamicLtxCompat =
            allowDynamicFrameCount
            && !string.IsNullOrWhiteSpace(sourceMedia.Compat?.ID)
            && sourceMedia.Compat.ID == genInfo.Vae.Compat?.ID;
        if (!sameVaeNode && !sameDynamicLtxCompat)
        {
            return false;
        }

        latentPath = WorkflowBridge.ToPath(samples);
        return true;
    }
}
