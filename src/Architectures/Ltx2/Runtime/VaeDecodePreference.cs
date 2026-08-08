using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2.Runtime;

internal static class VaeDecodePreference
{
    public static WGNodeData AsRawImage(
        WorkflowGenerator g,
        WGNodeData media,
        WGNodeData vae,
        string decodeId = null)
    {
        if (media is null)
        {
            return null;
        }
        if (media.DataType == WGNodeData.DT_IMAGE || media.DataType == WGNodeData.DT_VIDEO)
        {
            return media;
        }
        bool decodable = media.DataType == WGNodeData.DT_LATENT_IMAGE
            || media.DataType == WGNodeData.DT_LATENT_VIDEO
            // Narrower than core's Compat.HasJointAVLatents, which MiniMax also declares: core
            // would place the LTX-only LTXVSeparateAVLatent on a MiniMax joint latent.
            || (media.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO
                && media.IsCompat(T2IModelClassSorter.CompatLtxv2));
        if (vae is null || !decodable)
        {
            // No vae, audio-only, or a joint latent from another family: no LTX decode to place, so
            // a caller that claimed a host decode id does not get to spend it here. Nothing a stage
            // produces after sampling reaches this, and an unspent claim only leaves core's decode
            // to be swept.
            return media.AsRawImage(vae);
        }

        // Counted, because DecodeLatents mints its id from g.LastID: a counter-less bridge elsewhere
        // can leave that behind the workflow's real maximum, and the decode would land on a live node.
        using (WorkflowBridge bridge = BridgeSync.For(g))
        {
            INodeOutput targetVae = bridge.ResolvePath(vae.Path);
            if (targetVae is null || bridge.ResolvePath(media.Path) is null)
            {
                // Core asserts here; a stage whose vae or latent was swept keeps the media it had.
                return media;
            }
            if (bridge.NodeAt(media.Path) is IVaeEncode encode
                && encode.Vae.Connection is INodeOutput encodeVae
                && encodeVae.Node.Id == targetVae.Node.Id
                && encode.Pixels.Connection is INodeOutput pixels)
            {
                // Core's own short-circuit compares the two vae paths as JTokens, which Newtonsoft
                // resolves to reference identity, so it fires only when they are the same object.
                string rawDataType = media.DataType == WGNodeData.DT_LATENT_IMAGE
                    ? WGNodeData.DT_IMAGE
                    : WGNodeData.DT_VIDEO;
                return media.WithPath(NodeRef.Of(pixels).ToJArray(), rawDataType);
            }
        }
        return media.DecodeLatents(vae, false, decodeId);
    }
}
