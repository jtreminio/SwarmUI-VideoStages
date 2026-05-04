using ComfyTyped.Core;
using ComfyTyped.Generated;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal sealed class LtxAudioMaskResizer(
    WorkflowGenerator g,
    RootVideoStageResizer rootVideoStageResizer)
{
    internal void ApplyRootAudioMaskDimensionsAfterNativeVideo()
    {
        if (!rootVideoStageResizer.TryGetConfiguredRootStageResolution(out int width, out int height))
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        foreach (SetLatentNoiseMaskNode setMaskNode in bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>())
        {
            if (setMaskNode.Samples.Connection?.Node is not (LTXVAudioVAEEncodeNode or VAEEncodeAudioNode))
            {
                continue;
            }
            if (setMaskNode.Mask.Connection?.Node is not SolidMaskNode solidMask)
            {
                continue;
            }

            solidMask.Width.Set(width);
            solidMask.Height.Set(height);
            bridge.SyncNode(solidMask);
        }
    }

    internal void ApplyCurrentAudioMaskDimensions(WGNodeData media)
    {
        if (media?.Gen is not WorkflowGenerator generator
            || !media.Width.HasValue
            || !media.Height.HasValue
            || media.Path is not { Count: 2 } mediaPath)
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        INodeOutput output = bridge.ResolvePath(mediaPath);
        if (output?.Node is not LTXVConcatAVLatentNode concatNode
            || concatNode.AudioLatent.Connection?.Node is not SetLatentNoiseMaskNode setMaskNode
            || setMaskNode.Mask.Connection?.Node is not SolidMaskNode solidMask)
        {
            return;
        }

        solidMask.Width.Set(media.Width.Value);
        solidMask.Height.Set(media.Height.Value);
        bridge.SyncNode(solidMask);
    }
}
