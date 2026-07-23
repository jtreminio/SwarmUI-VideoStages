using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.LTX2;

internal sealed record LtxDecodeConfig(
    bool UseTiledDecode,
    int TileSize = 768,
    int Overlap = 64,
    int TemporalSize = 4096,
    int TemporalOverlap = 4);

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
        if (stageOutput?.Output is null)
        {
            return null;
        }

        LTXVSeparateAVLatentNode newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectFrom(stageOutput);
        bridge.SyncNode(newSeparate);

        ReplaceVideoDecode(
            bridge,
            capture.DecodeId,
            vae,
            newSeparate,
            decodeConfig);

        RetargetCapturedAudioDecode(bridge, capture, newSeparate);
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
        if (stageOutput?.Output is null)
        {
            return null;
        }

        LTXVSeparateAVLatentNode newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectFrom(stageOutput);
        bridge.SyncNode(newSeparate);

        if (vae?.Output is null)
        {
            return null;
        }

        ComfyNode dedicatedDecode =
            AddDecode(bridge, vae.Output, newSeparate.VideoLatent, decodeConfig);

        LTXVAudioVAEDecodeNode dedicatedAudioDecode = bridge.AddNode(
            new LTXVAudioVAEDecodeNode().With(Samples: newSeparate.AudioLatent));
        dedicatedAudioDecode.AudioVae.TryConnectToUntyped(capture.AudioVaeSource);
        bridge.SyncNode(dedicatedAudioDecode);

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
        if (currentMedia?.Output?.Node is null || audioVae?.Output is null)
        {
            return;
        }

        if (currentMedia.Output.Node is not IVaeDecode decode
            || decode.Samples.Connection?.Node is not LTXVSeparateAVLatentNode separate)
        {
            return;
        }

        LTXVAudioVAEDecodeNode audioDecode = bridge.AddNode(
            new LTXVAudioVAEDecodeNode().With(Samples: separate.AudioLatent));
        audioDecode.AudioVae.ConnectFrom(audioVae);
        bridge.SyncNode(audioDecode);

        currentMedia.AttachedAudio = new MediaRef
        {
            Output = audioDecode.Audio,
            DataType = WGNodeData.DT_AUDIO,
            Compat = audioVae.Compat
        };
    }

    internal static LtxDecodeConfig BuildDecodeConfig(WorkflowGenerator generator)
    {
        if (!generator.UserInput.TryGet(T2IParamTypes.VAETileSize, out _))
        {
            return new LtxDecodeConfig(false);
        }

        return new LtxDecodeConfig(
            UseTiledDecode: true,
            TileSize: generator.UserInput.Get(T2IParamTypes.VAETileSize, 768),
            Overlap: generator.UserInput.Get(T2IParamTypes.VAETileOverlap, 64),
            TemporalSize: generator.UserInput.Get(T2IParamTypes.VAETemporalTileSize, 4096),
            TemporalOverlap: generator.UserInput.Get(T2IParamTypes.VAETemporalTileOverlap, 4));
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
            bridge.SyncNode(added);
            return added;
        }

        VAEDecodeNode basic = new();
        VAEDecodeNode addedBasic = preserveId is not null
            ? bridge.AddNode(basic, preserveId)
            : bridge.AddNode(basic);
        addedBasic.Vae.ConnectToUntyped(vaeOutput);
        addedBasic.Samples.ConnectToUntyped(samplesOutput);
        bridge.SyncNode(addedBasic);
        return addedBasic;
    }

    private static void ReplaceVideoDecode(
        WorkflowBridge bridge,
        string decodeId,
        MediaRef vae,
        LTXVSeparateAVLatentNode newSeparate,
        LtxDecodeConfig decodeConfig)
    {
        if (string.IsNullOrWhiteSpace(decodeId) || vae?.Output is null)
        {
            return;
        }

        ComfyNode oldDecode = bridge.Graph.GetNode(decodeId);
        if (oldDecode is null)
        {
            return;
        }

        INodeOutput oldImageOutput = oldDecode.Outputs[0];
        bridge.RemoveNode(decodeId);

        ComfyNode newDecode = AddDecode(
            bridge,
            vae.Output,
            newSeparate.VideoLatent,
            decodeConfig,
            preserveId: decodeId);

        bridge.Graph.RetargetConnections(oldImageOutput, newDecode.Outputs[0]);
    }

    private static void RetargetCapturedAudioDecode(
        WorkflowBridge bridge,
        LtxChainCapture capture,
        LTXVSeparateAVLatentNode newSeparate)
    {
        if (capture.AudioDecodeId is null)
        {
            return;
        }

        LTXVSeparateAVLatentNode oldSeparate =
            bridge.Graph.GetNode<LTXVSeparateAVLatentNode>(capture.SeparateId);
        if (oldSeparate is not null)
        {
            int retargeted = bridge.Graph.RetargetConnections(
                oldSeparate.AudioLatent,
                newSeparate.AudioLatent,
                (node, input) => node.Id == capture.AudioDecodeId
                              && input.Name == "samples");
            if (retargeted > 0)
            {
                bridge.SyncNode(capture.AudioDecodeId);
            }
        }

        if (!HasAudioDecodeConnectedToSeparate(bridge, capture.AudioDecodeId, newSeparate.Id))
        {
            RetargetCapturedAudioDecodeViaJObject(
                bridge,
                capture.AudioDecodeId,
                newSeparate);
        }
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
}
