using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2;

internal static class LtxDecodeDefaults
{
    internal const int TileSize = 256;
    internal const int Overlap = 64;
    internal const int TemporalSize = 32;
    internal const int TemporalOverlap = 4;
    internal const int Ltx2TileSize = 2048;
    internal const int Ltx2Overlap = 256;
    internal const int Ltx2TemporalSize = 64;
    internal const int Ltx2TemporalOverlap = 16;
}

internal sealed record LtxDecodeConfig(
    bool UseTiledDecode,
    int TileSize = LtxDecodeDefaults.TileSize,
    int Overlap = LtxDecodeDefaults.Overlap,
    int TemporalSize = LtxDecodeDefaults.TemporalSize,
    int TemporalOverlap = LtxDecodeDefaults.TemporalOverlap)
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
                TileSize: generator.UserInput.Get(
                    T2IParamTypes.VAETileSize, LtxDecodeDefaults.TileSize),
                Overlap: generator.UserInput.Get(
                    T2IParamTypes.VAETileOverlap, LtxDecodeDefaults.Overlap),
                TemporalSize: generator.UserInput.Get(
                    T2IParamTypes.VAETemporalTileSize, LtxDecodeDefaults.TemporalSize),
                TemporalOverlap: generator.UserInput.Get(
                    T2IParamTypes.VAETemporalTileOverlap, LtxDecodeDefaults.TemporalOverlap));
        }
        if (generator.IsLTXV2()
            && generator.UserInput.Get(T2IParamTypes.ModelSpecificEnhancements, true))
        {
            return new LtxDecodeConfig(
                UseTiledDecode: true,
                TileSize: LtxDecodeDefaults.Ltx2TileSize,
                Overlap: LtxDecodeDefaults.Ltx2Overlap,
                TemporalSize: LtxDecodeDefaults.Ltx2TemporalSize,
                TemporalOverlap: LtxDecodeDefaults.Ltx2TemporalOverlap);
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
        if (!TryValidateCurrentOutputRecipe(
            bridge,
            currentOutputMedia,
            decode,
            oldSeparate,
            audioDecode,
            stageOutput,
            vae,
            decodeConfig,
            out CurrentOutputSpliceRecipe recipe))
        {
            return null;
        }

        LTXVSeparateAVLatentNode newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectToUntyped(recipe.StageOutput);

        ReplaceVideoDecode(
            bridge,
            recipe.Decode,
            recipe.VaeOutput,
            newSeparate,
            decodeConfig);

        RetargetCapturedAudioDecode(
            bridge,
            recipe.OldSeparate,
            recipe.AudioDecode,
            newSeparate);
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
        if (!TryValidateDedicatedBranchRecipe(
            bridge,
            currentOutputMedia,
            audioVaeSource,
            stageOutput,
            vae,
            decodeConfig,
            out DedicatedBranchSpliceRecipe recipe))
        {
            return null;
        }

        LTXVSeparateAVLatentNode newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectToUntyped(recipe.StageOutput);

        ComfyNode dedicatedDecode =
            AddDecode(bridge, recipe.VaeOutput, newSeparate.VideoLatent, decodeConfig);

        LTXVAudioVAEDecodeNode dedicatedAudioDecode = bridge.AddNode(
            new LTXVAudioVAEDecodeNode().With(Samples: newSeparate.AudioLatent));
        dedicatedAudioDecode.AudioVae.ConnectToUntyped(recipe.AudioVaeOutput);

        return new MediaRef
        {
            Output = dedicatedDecode.Outputs[0],
            DataType = WGNodeData.DT_VIDEO,
            Compat = vae?.Compat ?? currentOutputMedia.Compat,
            Width = outputWidth,
            Height = outputHeight,
            Frames = outputFrames ?? currentOutputMedia.Frames,
            FPS = outputFps ?? currentOutputMedia.FPS,
            AttachedAudio = new MediaRef
            {
                Output = dedicatedAudioDecode.Audio,
                DataType = WGNodeData.DT_AUDIO,
                Compat = currentOutputMedia.Compat
            }
        };
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

        LTXVAudioVAEDecodeNode audioDecode = bridge.AddNode(
            new LTXVAudioVAEDecodeNode().With(Samples: separate.AudioLatent));
        audioDecode.AudioVae.ConnectFrom(audioVae);

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

    private static bool TryValidateCurrentOutputRecipe(
        WorkflowBridge bridge,
        MediaRef currentOutputMedia,
        ComfyNode decode,
        LTXVSeparateAVLatentNode oldSeparate,
        ComfyNode audioDecode,
        MediaRef stageOutput,
        MediaRef vae,
        LtxDecodeConfig decodeConfig,
        out CurrentOutputSpliceRecipe recipe)
    {
        recipe = null;
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

        recipe = new(
            stageOutput.Output,
            vae.Output,
            decode,
            oldSeparate,
            audioDecode);
        return true;
    }

    private static bool TryValidateDedicatedBranchRecipe(
        WorkflowBridge bridge,
        MediaRef currentOutputMedia,
        INodeOutput audioVaeSource,
        MediaRef stageOutput,
        MediaRef vae,
        LtxDecodeConfig decodeConfig,
        out DedicatedBranchSpliceRecipe recipe)
    {
        recipe = null;
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

        recipe = new(
            stageOutput.Output,
            vae.Output,
            audioVaeSource);
        return true;
    }

    private static bool IsOutputInGraph(WorkflowBridge bridge, INodeOutput output) =>
        output?.Node?.Id is string id
        && bridge.Graph.GetNode(id) is ComfyNode node
        && output.SlotIndex >= 0
        && output.SlotIndex < node.Outputs.Count;

    private sealed record CurrentOutputSpliceRecipe(
        INodeOutput StageOutput,
        INodeOutput VaeOutput,
        ComfyNode Decode,
        LTXVSeparateAVLatentNode OldSeparate,
        ComfyNode AudioDecode);

    private sealed record DedicatedBranchSpliceRecipe(
        INodeOutput StageOutput,
        INodeOutput VaeOutput,
        INodeOutput AudioVaeOutput);
}
