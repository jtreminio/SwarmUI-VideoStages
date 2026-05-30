using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.WAN;

internal static class WanLatentReuse
{
    internal sealed class Capture
    {
        public JArray LatentPath { get; set; }
    }

    internal static bool TryResolveReusableLatent(
        WorkflowGenerator g,
        WGNodeData sourceMedia,
        WGNodeData vae,
        out WGNodeData reusedLatent)
    {
        reusedLatent = null;
        if (sourceMedia?.Path is null || vae?.Path is null)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput vaeOutput = bridge.ResolvePath(vae.Path);
        (INodeOutput samples, INodeOutput decodeVae) =
            bridge.ResolvePath(sourceMedia.Path)?.Node is IVaeDecode decode
                ? (decode.Samples.Connection, decode.Vae.Connection)
                : (null, null);
        if (samples is null || !SameOutput(decodeVae, vaeOutput))
        {
            return false;
        }

        reusedLatent = sourceMedia.WithPath(WorkflowBridge.ToPath(samples), WGNodeData.DT_LATENT_VIDEO, vae.Compat);
        return true;
    }

    internal static void ReapplyToSampler(WorkflowGenerator g, Capture capture)
    {
        if (capture?.LatentPath is null || g.CurrentMedia?.Path is null)
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput reuseOutput = bridge.ResolvePath(capture.LatentPath);
        ComfyNode mediaNode = bridge.ResolvePath(g.CurrentMedia.Path)?.Node;
        SwarmKSamplerNode sampler = mediaNode as SwarmKSamplerNode
            ?? (mediaNode is not null ? bridge.Graph.FindNearestUpstream<SwarmKSamplerNode>(mediaNode) : null);
        if (reuseOutput is null
            || sampler?.LatentImage.Connection is not INodeOutput currentLatent
            || SameOutput(currentLatent, reuseOutput))
        {
            return;
        }

        ComfyNode staleLatentNode = currentLatent.Node;
        sampler.LatentImage.ConnectToUntyped(reuseOutput);
        bridge.SyncNode(sampler);

        HashSet<string> protectedNodes = [reuseOutput.Node.Id, mediaNode.Id];
        WorkflowGraphCleanup.RemoveUnusedUpstreamNodes(bridge, staleLatentNode.Id, protectedNodes);
    }

    private static bool SameOutput(INodeOutput a, INodeOutput b) =>
        a is not null && b is not null && a.Node == b.Node && a.SlotIndex == b.SlotIndex;
}
