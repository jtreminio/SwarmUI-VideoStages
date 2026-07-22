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
    /// video is flat log-encoded footage. The postprocess's linear HDR output feeds our own
    /// SwarmSaveHDRAnimationWS node (PQ-encodes to Rec.2020 and writes a 10-bit HDR10 mp4).
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
            if (save.Images.Connection is not INodeOutput imagesSource)
            {
                continue;
            }
            LTXVHDRDecodePostprocessNode post = bridge.AddNode(new LTXVHDRDecodePostprocessNode());
            post.Image.ConnectToUntyped(imagesSource);

            SwarmSaveHDRAnimationWSNode hdrSave = bridge.AddNode(new SwarmSaveHDRAnimationWSNode());
            hdrSave.Images.ConnectTo(post.HdrLinear);
            if (save.Fps.Connection is INodeOutput fpsSource)
            {
                hdrSave.Fps.ConnectToUntyped(fpsSource);
            }
            else if (save.Fps.LiteralAsDouble() is double fps)
            {
                hdrSave.Fps.Set(fps);
            }
            if (save.Audio.Connection is INodeOutput audioSource)
            {
                hdrSave.Audio.ConnectToUntyped(audioSource);
            }

            bridge.SyncNode(post);
            bridge.SyncNode(hdrSave);
            bridge.RemoveNode(save);
        }
    }

    private static bool HasActiveHdrIcLora(ClipSpec clip) => clip.IcLoras?.Any(entry =>
        StringUtils.Equals(entry.Preset?.Trim(), "hdr")
        || (entry.Lora?.Contains("ic-lora-hdr", StringComparison.OrdinalIgnoreCase) ?? false)) == true;
}
