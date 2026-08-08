using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2.Runtime.Chain;

/// <summary>
/// The tiling geometry of one LTX decode. The numbers restate core's own, hard-coded inside
/// <c>WGNodeData.DecodeLatents</c> with nothing public to call; splicing a decode by id needs the
/// node built through the bridge, so core cannot build it for us. `Decode_tiling_geometry_matches_core`
/// asserts the two still agree.
/// </summary>
internal sealed record LtxDecodeConfig(
    bool UseTiledDecode,
    int TileSize = 0,
    int Overlap = 0,
    int TemporalSize = 0,
    int TemporalOverlap = 0)
{
    internal static LtxDecodeConfig From(WorkflowGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        bool hasExplicitTiling =
            generator.UserInput.TryGet(T2IParamTypes.VAETileSize, out _)
            || generator.UserInput.TryGet(T2IParamTypes.VAETemporalTileSize, out _);
        if (hasExplicitTiling)
        {
            return new LtxDecodeConfig(
                UseTiledDecode: true,
                TileSize: generator.UserInput.Get(T2IParamTypes.VAETileSize, 256),
                Overlap: generator.UserInput.Get(T2IParamTypes.VAETileOverlap, 64),
                TemporalSize: generator.UserInput.Get(T2IParamTypes.VAETemporalTileSize, 32),
                TemporalOverlap: generator.UserInput.Get(
                    T2IParamTypes.VAETemporalTileOverlap, 4));
        }
        if (generator.IsLTXV2()
            && generator.UserInput.Get(T2IParamTypes.ModelSpecificEnhancements, true))
        {
            return new LtxDecodeConfig(
                UseTiledDecode: true,
                TileSize: 2048,
                Overlap: 256,
                TemporalSize: 64,
                TemporalOverlap: 16);
        }
        return new LtxDecodeConfig(UseTiledDecode: false);
    }
}

internal static class LtxPostChainRebuilder
{
    public static MediaRef SpliceCurrentOutput(
        WorkflowBridge bridge,
        MediaRef currentOutputMedia,
        ComfyNode decode,
        LTXVSeparateAVLatentNode oldSeparate,
        ComfyNode audioDecode,
        MediaRef stageOutput,
        MediaRef vae,
        LtxDecodeConfig decodeConfig)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (!CanSpliceCurrentOutput(
            bridge,
            currentOutputMedia,
            decode,
            oldSeparate,
            audioDecode,
            stageOutput,
            vae,
            decodeConfig))
        {
            return null;
        }

        LTXVSeparateAVLatentNode newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectToUntyped(stageOutput.Output);

        ReplaceVideoDecode(bridge, decode, vae.Output, newSeparate, decodeConfig);

        RetargetCapturedAudioDecode(bridge, oldSeparate, audioDecode, newSeparate);
        return currentOutputMedia.Clone();
    }

    public static MediaRef SpliceCurrentOutputToDedicatedBranch(
        WorkflowBridge bridge,
        MediaRef currentOutputMedia,
        INodeOutput audioVaeSource,
        MediaRef stageOutput,
        MediaRef vae,
        LtxDecodeConfig decodeConfig,
        int outputWidth,
        int outputHeight,
        int? outputFrames,
        int? outputFps)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (!CanSpliceToDedicatedBranch(
            bridge,
            currentOutputMedia,
            audioVaeSource,
            stageOutput,
            vae,
            decodeConfig))
        {
            return null;
        }

        LTXVSeparateAVLatentNode newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectToUntyped(stageOutput.Output);

        ComfyNode dedicatedDecode =
            AddDecode(bridge, vae.Output, newSeparate.VideoLatent, decodeConfig);

        MediaRef result = new()
        {
            Output = dedicatedDecode.Outputs[0],
            DataType = WGNodeData.DT_VIDEO,
            Compat = vae?.Compat ?? currentOutputMedia.Compat,
            Width = outputWidth,
            Height = outputHeight,
            Frames = outputFrames ?? currentOutputMedia.Frames,
            FPS = outputFps ?? currentOutputMedia.FPS,
        };
        AttachDecodedLtxAudio(
            bridge,
            result,
            new MediaRef
            {
                Output = audioVaeSource,
                DataType = WGNodeData.DT_AUDIOVAE,
                Compat = currentOutputMedia.Compat
            });
        return result;
    }

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
        LtxDecodeConfig config,
        string preserveId = null)
    {
        if (config.UseTiledDecode)
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

    private static void ReplaceVideoDecode(
        WorkflowBridge bridge,
        ComfyNode oldDecode,
        INodeOutput vaeOutput,
        LTXVSeparateAVLatentNode newSeparate,
        LtxDecodeConfig decodeConfig)
    {
        INodeOutput oldImageOutput = oldDecode.Outputs[0];
        // Keep the decode id so existing consumers and cached references stay valid.
        bridge.RemoveNode(oldDecode.Id);

        ComfyNode newDecode = AddDecode(
            bridge,
            vaeOutput,
            newSeparate.VideoLatent,
            decodeConfig,
            preserveId: oldDecode.Id);

        bridge.Graph.RetargetConnections(oldImageOutput, newDecode.Outputs[0]);
    }

    private static void RetargetCapturedAudioDecode(
        WorkflowBridge bridge,
        LTXVSeparateAVLatentNode oldSeparate,
        ComfyNode audioDecode,
        LTXVSeparateAVLatentNode newSeparate)
    {
        if (audioDecode is null)
        {
            return;
        }

        bridge.Graph.RetargetConnections(
            oldSeparate.AudioLatent,
            newSeparate.AudioLatent,
            (node, input) => node.Id == audioDecode.Id
                          && input.Name == "samples");
    }

    private static bool CanSpliceCurrentOutput(
        WorkflowBridge bridge,
        MediaRef currentOutputMedia,
        ComfyNode decode,
        LTXVSeparateAVLatentNode oldSeparate,
        ComfyNode audioDecode,
        MediaRef stageOutput,
        MediaRef vae,
        LtxDecodeConfig decodeConfig)
    {
        if (currentOutputMedia?.Output is null
            || stageOutput?.Output is null
            || vae?.Output is null
            || decodeConfig is null
            || decode is null
            || oldSeparate is null
            || !IsOutputInGraph(bridge, currentOutputMedia.Output)
            || !IsOutputInGraph(bridge, stageOutput.Output)
            || !IsOutputInGraph(bridge, vae.Output))
        {
            return false;
        }

        if (bridge.Graph.GetNode(decode.Id) is not ComfyNode
            || decode is not IVaeDecode vaeDecode
            || decode.Outputs.Count == 0
            || bridge.Graph.GetNode<LTXVSeparateAVLatentNode>(oldSeparate.Id) is null
            || vaeDecode.Samples.Connection?.Node?.Id != oldSeparate.Id)
        {
            return false;
        }

        if (audioDecode is not null)
        {
            if (bridge.Graph.GetNode(audioDecode.Id) is not ComfyNode
                || audioDecode.FindInput("samples")?.Connection?.Node?.Id
                    != oldSeparate.Id)
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanSpliceToDedicatedBranch(
        WorkflowBridge bridge,
        MediaRef currentOutputMedia,
        INodeOutput audioVaeSource,
        MediaRef stageOutput,
        MediaRef vae,
        LtxDecodeConfig decodeConfig)
    {
        if (currentOutputMedia is null
            || stageOutput?.Output is null
            || vae?.Output is null
            || audioVaeSource is null
            || decodeConfig is null
            || !IsOutputInGraph(bridge, stageOutput.Output)
            || !IsOutputInGraph(bridge, vae.Output)
            || !IsOutputInGraph(bridge, audioVaeSource))
        {
            return false;
        }

        return true;
    }

    private static bool IsOutputInGraph(WorkflowBridge bridge, INodeOutput output) =>
        output?.Node?.Id is string id
        && bridge.Graph.GetNode(id) is ComfyNode node
        && output.SlotIndex >= 0
        && output.SlotIndex < node.Outputs.Count;
}
