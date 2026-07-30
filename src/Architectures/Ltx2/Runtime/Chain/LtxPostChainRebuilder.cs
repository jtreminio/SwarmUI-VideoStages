using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2;

/// <summary>SwarmUI's own runtime VAE-decode tiling fallbacks, mirrored so VideoStages decodes
/// look like every other Swarm decode.</summary>
internal static class LtxDecodeDefaults
{
    internal const int TileSize = 256;
    internal const int Overlap = 64;
    internal const int TemporalSize = 32;
    internal const int TemporalOverlap = 4;
}

/// <summary>
/// The single owner of "how does this generation decode latents". Every VideoStages decode - the
/// spliced post-chain rebuild and the ad-hoc <see cref="VaeDecodePreference"/> path alike - reads
/// its tiling decision from here, so a stage cannot get different tile geometry purely because of
/// which builder produced its decode node.
/// </summary>
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
        if (!generator.UserInput.TryGet(T2IParamTypes.VAETileSize, out _))
        {
            return new LtxDecodeConfig(UseTiledDecode: false);
        }
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
}

/// <summary>
/// Rebuilds decode branches and retargets consumers after a stage replaces the AV latent.
/// </summary>
internal static class LtxPostChainRebuilder
{
    public static MediaRef SpliceCurrentOutput(
        WorkflowBridge bridge,
        LtxChainCapture capture,
        MediaRef stageOutput,
        MediaRef vae,
        LtxDecodeConfig decodeConfig)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (!TryValidateCurrentOutputRecipe(
            bridge,
            capture,
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
            capture,
            recipe.OldSeparate,
            recipe.AudioDecode,
            newSeparate);
        return capture.CurrentOutputMedia.Clone();
    }

    public static MediaRef SpliceCurrentOutputToDedicatedBranch(
        WorkflowBridge bridge,
        LtxChainCapture capture,
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
            capture,
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
            Compat = vae?.Compat ?? capture.CurrentOutputMedia.Compat,
            Width = outputWidth,
            Height = outputHeight,
            Frames = outputFrames ?? capture.CurrentOutputMedia.Frames,
            FPS = outputFps ?? capture.CurrentOutputMedia.FPS,
            AttachedAudio = new MediaRef
            {
                Output = dedicatedAudioDecode.Audio,
                DataType = WGNodeData.DT_AUDIO,
                Compat = capture.AudioVaeSource?.Node is not null
                    ? capture.CurrentOutputMedia.Compat
                    : null
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
        // Deliberately not routed through VideoGraphHelpers.RemoveNode: the replacement decode is
        // re-added under the same id below, so cached references to it stay valid.
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
        LtxChainCapture capture,
        LTXVSeparateAVLatentNode oldSeparate,
        ComfyNode audioDecode,
        LTXVSeparateAVLatentNode newSeparate)
    {
        if (capture.AudioDecodeId is null)
        {
            return;
        }

        bridge.Graph.RetargetConnections(
            oldSeparate.AudioLatent,
            newSeparate.AudioLatent,
            (node, input) => node.Id == audioDecode.Id
                          && input.Name == "samples");

        if (!HasAudioDecodeConnectedToSeparate(bridge, audioDecode.Id, newSeparate.Id))
        {
            RetargetCapturedAudioDecodeViaJObject(
                bridge,
                audioDecode.Id,
                newSeparate);
        }
    }

    private static bool TryValidateCurrentOutputRecipe(
        WorkflowBridge bridge,
        LtxChainCapture capture,
        MediaRef stageOutput,
        MediaRef vae,
        LtxDecodeConfig decodeConfig,
        out CurrentOutputSpliceRecipe recipe)
    {
        recipe = null;
        if (capture?.CurrentOutputMedia?.Output is null
            || stageOutput?.Output is null
            || vae?.Output is null
            || decodeConfig is null
            || string.IsNullOrWhiteSpace(capture.DecodeId)
            || string.IsNullOrWhiteSpace(capture.SeparateId)
            || !IsOutputInGraph(bridge, capture.CurrentOutputMedia.Output)
            || !IsOutputInGraph(bridge, stageOutput.Output)
            || !IsOutputInGraph(bridge, vae.Output))
        {
            return false;
        }

        if (bridge.Graph.GetNode(capture.DecodeId) is not ComfyNode decode
            || decode is not IVaeDecode vaeDecode
            || decode.Outputs.Count == 0
            || bridge.Graph.GetNode<LTXVSeparateAVLatentNode>(capture.SeparateId)
                is not LTXVSeparateAVLatentNode oldSeparate
            || vaeDecode.Samples.Connection?.Node?.Id != oldSeparate.Id)
        {
            return false;
        }

        ComfyNode audioDecode = null;
        if (capture.AudioDecodeId is not null)
        {
            if (bridge.Graph.GetNode(capture.AudioDecodeId) is not ComfyNode resolvedAudioDecode
                || bridge.Workflow[capture.AudioDecodeId] is not JObject audioDecodeObject
                || !AudioDecodeReadsSeparate(
                    resolvedAudioDecode,
                    audioDecodeObject,
                    oldSeparate.Id))
            {
                return false;
            }
            audioDecode = resolvedAudioDecode;
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
        LtxChainCapture capture,
        MediaRef stageOutput,
        MediaRef vae,
        LtxDecodeConfig decodeConfig,
        out DedicatedBranchSpliceRecipe recipe)
    {
        recipe = null;
        if (capture?.CurrentOutputMedia is null
            || stageOutput?.Output is null
            || vae?.Output is null
            || capture.AudioVaeSource is null
            || decodeConfig is null
            || !IsOutputInGraph(bridge, stageOutput.Output)
            || !IsOutputInGraph(bridge, vae.Output)
            || !IsOutputInGraph(bridge, capture.AudioVaeSource))
        {
            return false;
        }

        recipe = new(
            stageOutput.Output,
            vae.Output,
            capture.AudioVaeSource);
        return true;
    }

    private static bool IsOutputInGraph(WorkflowBridge bridge, INodeOutput output) =>
        output?.Node?.Id is string id
        && bridge.Graph.GetNode(id) is ComfyNode node
        && output.SlotIndex >= 0
        && output.SlotIndex < node.Outputs.Count;

    private static bool AudioDecodeReadsSeparate(
        ComfyNode audioDecode,
        JObject audioDecodeObject,
        string separateId)
    {
        if (audioDecode.FindInput("samples")?.Connection?.Node?.Id == separateId)
        {
            return true;
        }
        return audioDecodeObject["inputs"]?["samples"] is JArray samples
            && samples.Count >= 2
            && $"{samples[0]}" == separateId;
    }

    private static void RetargetCapturedAudioDecodeViaJObject(
        WorkflowBridge bridge,
        string audioDecodeId,
        LTXVSeparateAVLatentNode newSeparate)
    {
        if (string.IsNullOrWhiteSpace(audioDecodeId)
            || bridge.Workflow[audioDecodeId] is not JObject audioDecode)
        {
            return;
        }

        JObject inputs = audioDecode["inputs"] as JObject;
        if (inputs is null)
        {
            inputs = [];
            audioDecode["inputs"] = inputs;
        }

        inputs["samples"] = WorkflowBridge.ToPath(newSeparate.AudioLatent);
    }

    private static bool HasAudioDecodeConnectedToSeparate(
        WorkflowBridge bridge,
        string audioDecodeId,
        string separateId)
    {
        ComfyNode audioNode = bridge.Graph.GetNode(audioDecodeId);
        if (audioNode is null)
        {
            return false;
        }

        INodeInput samplesInput = audioNode.FindInput("samples");
        return samplesInput?.Connection?.Node?.Id == separateId;
    }

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
