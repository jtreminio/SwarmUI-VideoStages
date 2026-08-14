using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2.Runtime.Chain;

internal static class LtxPostChainRebuilder
{
    public static void AttachDecodedLtxAudio(
        WorkflowBridge bridge,
        MediaRef currentMedia,
        MediaRef audioVae)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (currentMedia?.Output?.Node is null || audioVae?.Output is null)
        {
            return;
        }

        if (bridge.Graph.GetNode(currentMedia.Output.Node.Id) is not IVaeDecode decode
            || decode.Samples.Connection?.Node is not LTXVSeparateAVLatentNode separate)
        {
            return;
        }
        if (!IsOutputInGraph(bridge, audioVae.Output)
            || bridge.Graph.GetNode(separate.Id) is not LTXVSeparateAVLatentNode)
        {
            return;
        }

        LTXVAudioVAEDecodeNode audioDecode = bridge.Graph
            .NodesOfType<LTXVAudioVAEDecodeNode>()
            .FirstOrDefault(node =>
                ReferenceEquals(node.Samples.Connection, separate.AudioLatent)
                && ReferenceEquals(node.AudioVae.Connection, audioVae.Output));
        if (audioDecode is null)
        {
            audioDecode = bridge.AddNode(
                new LTXVAudioVAEDecodeNode().With(Samples: separate.AudioLatent));
            audioDecode.AudioVae.ConnectFrom(audioVae);
        }

        currentMedia.AttachedAudio = new MediaRef
        {
            Output = audioDecode.Audio,
            DataType = WGNodeData.DT_AUDIO,
            Compat = audioVae.Compat
        };
    }

    internal static ComfyNode AddDecode(
        WorkflowBridge bridge,
        INodeOutput vaeOutput,
        INodeOutput samplesOutput,
        LtxVaeTilingConfig config,
        string preserveId = null)
    {
        if (config.Enabled)
        {
            VAEDecodeTiledNode tiled = new VAEDecodeTiledNode().With(
                TileSize: config.TileSize,
                Overlap: config.Overlap,
                TemporalSize: config.TemporalSize,
                TemporalOverlap: config.TemporalOverlap);
            VAEDecodeTiledNode added = preserveId is not null
                ? bridge.AddNode(tiled, preserveId)
                : bridge.AddNode(tiled);
            added.Vae.ConnectToUntyped(vaeOutput);
            added.Samples.ConnectToUntyped(samplesOutput);
            return added;
        }

        VAEDecodeNode basic = new();
        VAEDecodeNode addedBasic = preserveId is not null
            ? bridge.AddNode(basic, preserveId)
            : bridge.AddNode(basic);
        addedBasic.Vae.ConnectToUntyped(vaeOutput);
        addedBasic.Samples.ConnectToUntyped(samplesOutput);
        return addedBasic;
    }

    private static bool IsOutputInGraph(WorkflowBridge bridge, INodeOutput output) =>
        output?.Node?.Id is string id
        && bridge.Graph.GetNode(id) is ComfyNode node
        && output.SlotIndex >= 0
        && output.SlotIndex < node.Outputs.Count;
}
