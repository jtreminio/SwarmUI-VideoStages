using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal class HdrPostprocessApplicator(WorkflowGenerator g)
{
    /// <summary>
    /// Splices the HDR LogC3 postprocess (pure math, no aux weights) between the decoded frames
    /// and the final owned animation saves when any clip has an active HDR IC-LoRA — without it the saved
    /// video is flat log-encoded footage. The postprocess's linear HDR output feeds our own
    /// SwarmSaveHDRAnimationWS node (PQ-encodes to Rec.2020 and writes a 10-bit HDR10 mp4).
    /// </summary>
    public void ApplyHdrPostprocessToFinalSaves(
        VideoExecutionPlan plan,
        IReadOnlySet<string> finalSaveNodeIds)
    {
        if (plan is null
            || finalSaveNodeIds is null
            || finalSaveNodeIds.Count == 0
            || !plan.Clips.Any(HdrIcLoraPolicy.IsActive))
        {
            return;
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        foreach (SwarmSaveAnimationWSNode save in finalSaveNodeIds
            .Select(id => bridge.Graph.GetNode(id))
            .OfType<SwarmSaveAnimationWSNode>())
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

            VideoGraphHelpers.RemoveNode(g, bridge, save.Id);
        }
    }
}
