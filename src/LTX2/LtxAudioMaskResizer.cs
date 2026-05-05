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
        foreach (SetLatentNoiseMaskNode setMask in bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>())
        {
            if (setMask.Samples.Connection?.Node is not (LTXVAudioVAEEncodeNode or VAEEncodeAudioNode))
            {
                continue;
            }

            if (setMask.Mask.Connection?.Node is not SolidMaskNode solidMask)
            {
                continue;
            }

            solidMask.Width.Set(width);
            solidMask.Height.Set(height);
            bridge.SyncNode(solidMask);
        }
    }

    internal static void ApplyCurrentAudioMaskDimensions(WGNodeData media)
    {
        if (media?.Gen is not WorkflowGenerator generator)
        {
            return;
        }

        if (!media.Width.HasValue || !media.Height.HasValue)
        {
            return;
        }

        if (media.Path is not { Count: 2 } mediaPath)
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        INodeOutput output = bridge.ResolvePath(mediaPath);
        if (output?.Node is not LTXVConcatAVLatentNode concat)
        {
            return;
        }

        if (concat.AudioLatent.Connection?.Node is not SetLatentNoiseMaskNode setMask
            || setMask.Mask.Connection?.Node is not SolidMaskNode solidMask)
        {
            return;
        }

        solidMask.Width.Set(media.Width.Value);
        solidMask.Height.Set(media.Height.Value);
        bridge.SyncNode(solidMask);
    }
}
