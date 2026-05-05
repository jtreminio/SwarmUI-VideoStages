using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal static class LtxChainOps
{
    public static LtxChainCapture TryCapture(
        WorkflowBridge bridge,
        MediaRef currentMedia,
        MediaRef currentAudioVae,
        bool useReusedAudio)
    {
        if (currentMedia?.Output?.Node is not ComfyNode mediaNode)
        {
            return null;
        }

        ComfyNode decode = mediaNode as VAEDecodeNode
            ?? mediaNode as VAEDecodeTiledNode
            ?? bridge.Graph.FindNearestUpstream<VAEDecodeNode>(mediaNode)
            ?? (ComfyNode)bridge.Graph.FindNearestUpstream<VAEDecodeTiledNode>(mediaNode);
        if (decode is null)
        {
            return null;
        }

        INodeInput samplesInput = decode.FindInput("samples");
        if (samplesInput?.Connection?.Node is not LTXVSeparateAVLatentNode separate)
        {
            return null;
        }

        if (separate.AvLatent.Connection is null)
        {
            return null;
        }
        INodeInput vaeInput = decode.FindInput("vae");
        if (vaeInput?.Connection is null)
        {
            return null;
        }

        LTXVAudioVAEDecodeNode audioDecode = bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>()
            .FirstOrDefault(n =>
                n.Samples.Connection?.Node == separate
                && n.Samples.Connection?.SlotIndex == 1);

        INodeOutput audioVaeSource = audioDecode?.AudioVae.Connection ?? currentAudioVae?.Output;
        if (audioVaeSource is null)
        {
            return null;
        }

        bool hasPostDecodeWrappers = !ReferenceEquals(currentMedia.Output.Node, decode);

        return new LtxChainCapture(
            DecodeId: decode.Id,
            SeparateId: separate.Id,
            AudioDecodeId: audioDecode?.Id,
            AudioVaeSource: audioVaeSource,
            CurrentOutputMedia: currentMedia.Clone(),
            HasPostDecodeWrappers: hasPostDecodeWrappers,
            UseReusedAudio: useReusedAudio);
    }

    internal sealed record DecodeConfig(
        bool UseTiledDecode,
        int TileSize = 768,
        int Overlap = 64,
        int TemporalSize = 4096,
        int TemporalOverlap = 4);

    public static MediaRef SpliceCurrentOutput(
        WorkflowBridge bridge,
        LtxChainCapture capture,
        MediaRef stageOutput,
        MediaRef vae,
        DecodeConfig decodeConfig)
    {
        if (stageOutput?.Output is null)
        {
            return null;
        }

        LTXVSeparateAVLatentNode newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectToUntyped(stageOutput.Output);
        bridge.SyncNode(newSeparate);

        ReplaceVideoDecode(
            bridge,
            capture.DecodeId,
            vae,
            newSeparate,
            decodeConfig);

        if (capture.AudioDecodeId is not null)
        {
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
                RetargetCapturedAudioDecodeViaJObject(bridge, capture.AudioDecodeId, newSeparate);
            }
        }

        return capture.CurrentOutputMedia.Clone();
    }

    public static MediaRef SpliceCurrentOutputToDedicatedBranch(
        WorkflowBridge bridge,
        LtxChainCapture capture,
        MediaRef stageOutput,
        MediaRef vae,
        DecodeConfig decodeConfig,
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
        newSeparate.AvLatent.ConnectToUntyped(stageOutput.Output);
        bridge.SyncNode(newSeparate);

        if (vae?.Output is null)
        {
            return null;
        }

        ComfyNode dedicatedDecode = AddDecode(
            bridge, vae.Output, newSeparate.VideoLatent, decodeConfig);

        LTXVAudioVAEDecodeNode dedicatedAudioDecode = bridge.AddNode(new LTXVAudioVAEDecodeNode());
        dedicatedAudioDecode.Samples.ConnectTo(newSeparate.AudioLatent);
        if (capture.AudioVaeSource is not null)
        {
            dedicatedAudioDecode.AudioVae.ConnectToUntyped(capture.AudioVaeSource);
        }
        bridge.SyncNode(dedicatedAudioDecode);

        MediaRef decodedVideo = new()
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

        return decodedVideo;
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

        ComfyNode decodeNode = currentMedia.Output.Node;
        INodeInput samplesInput = decodeNode.FindInput("samples");
        if (samplesInput is null
            || decodeNode is not VAEDecodeNode && decodeNode is not VAEDecodeTiledNode
            || samplesInput.Connection?.Node is not LTXVSeparateAVLatentNode separate)
        {
            return;
        }

        LTXVAudioVAEDecodeNode audioDecode = bridge.AddNode(new LTXVAudioVAEDecodeNode());
        audioDecode.Samples.ConnectTo(separate.AudioLatent);
        audioDecode.AudioVae.ConnectToUntyped(audioVae.Output);
        bridge.SyncNode(audioDecode);

        currentMedia.AttachedAudio = new MediaRef
        {
            Output = audioDecode.Audio,
            DataType = WGNodeData.DT_AUDIO,
            Compat = audioVae.Compat
        };
    }

    public static void RetargetAnimationSaves(
        WorkflowBridge bridge,
        INodeOutput oldOutput,
        INodeOutput newOutput)
    {
        if (oldOutput is null || newOutput is null)
        {
            return;
        }

        bridge.Graph.RetargetConnections(
            oldOutput,
            newOutput,
            (node, input) => node is SwarmSaveAnimationWSNode && input.Name == "images");

        foreach (ComfyNode downstream in bridge.Graph.FindDownstream(newOutput))
        {
            if (downstream is SwarmSaveAnimationWSNode)
            {
                bridge.SyncNode(downstream);
            }
        }
    }

    private static void ReplaceVideoDecode(
        WorkflowBridge bridge,
        string decodeId,
        MediaRef vae,
        LTXVSeparateAVLatentNode newSeparate,
        DecodeConfig decodeConfig)
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
            bridge, vae.Output, newSeparate.VideoLatent, decodeConfig, preserveId: decodeId);

        bridge.Graph.RetargetConnections(oldImageOutput, newDecode.Outputs[0]);
    }

    private static ComfyNode AddDecode(
        WorkflowBridge bridge,
        INodeOutput vaeOutput,
        INodeOutput samplesOutput,
        DecodeConfig config,
        string preserveId = null)
    {
        if (config.UseTiledDecode)
        {
            VAEDecodeTiledNode tiled = new();
            tiled.TileSize.Set(config.TileSize);
            tiled.Overlap.Set(config.Overlap);
            tiled.TemporalSize.Set(config.TemporalSize);
            tiled.TemporalOverlap.Set(config.TemporalOverlap);
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

    private static void RetargetCapturedAudioDecodeViaJObject(
        WorkflowBridge bridge,
        string audioDecodeId,
        LTXVSeparateAVLatentNode newSeparate)
    {
        if (string.IsNullOrWhiteSpace(audioDecodeId))
        {
            return;
        }

        if (bridge.Workflow[audioDecodeId] is not JObject audioDecode)
        {
            return;
        }

        JObject inputs = audioDecode["inputs"] as JObject;
        if (inputs is null)
        {
            inputs = [];
            audioDecode["inputs"] = inputs;
        }

        JArray samplesRef = WorkflowBridge.ToPath(newSeparate.AudioLatent);
        inputs["samples"] = new JArray(samplesRef[0], samplesRef[1]);
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
