using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;

namespace VideoStages;

internal class HdrPostprocessApplicator(WorkflowGenerator g)
{
    /// <summary>
    /// Splices the HDR LogC3 postprocess (pure math, no aux weights) between the decoded frames
    /// and every animation save when any clip has an active HDR IC-LoRA — without it the saved
    /// video is flat log-encoded footage. The tonemapped SDR output feeds the save; EXR export
    /// stays off (Swarm's save path has no 16-bit format). Spec-wide: mixing HDR and non-HDR
    /// clips in one run is inherently inconsistent, so every save gets the same treatment.
    /// </summary>
    public void ApplyHdrPostprocessToFinalSaves(IReadOnlyList<ClipSpec> clips)
    {
        if (clips is null || !clips.Any(HasActiveHdrIcLora))
        {
            return;
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        foreach (SwarmSaveAnimationWSNode save in bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>())
        {
            if (save.Images.Connection is not INodeOutput imagesSource
                || imagesSource.Node is LTXVHDRDecodePostprocessNode)
            {
                continue;
            }
            LTXVHDRDecodePostprocessNode post = bridge.AddNode(new LTXVHDRDecodePostprocessNode());
            post.Image.ConnectToUntyped(imagesSource);
            save.Images.ConnectTo(post.Tonemapped);
            bridge.SyncNode(post);
            bridge.SyncNode(save);
        }
    }

    private static bool HasActiveHdrIcLora(ClipSpec clip) => clip.IcLoras?.Any(entry =>
        StringUtils.Equals(entry.Preset?.Trim(), "hdr")
        || (entry.Lora?.Contains("ic-lora-hdr", StringComparison.OrdinalIgnoreCase) ?? false)) == true;
}
