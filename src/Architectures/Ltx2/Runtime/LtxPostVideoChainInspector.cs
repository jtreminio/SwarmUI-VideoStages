using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Performs read-only inspection of the graph around the current LTX video output.
/// </summary>
internal static class LtxPostVideoChainInspector
{
    public static LtxPostVideoChainState TryCapture(
        WorkflowGenerator generator,
        bool useReusedAudio)
    {
        if (generator.CurrentMedia?.IsRawMedia != true
            || generator.CurrentMedia.Path is not JArray { Count: 2 })
        {
            return null;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        MediaRef currentMedia = MediaRef.FromWGNodeData(generator.CurrentMedia, bridge);
        MediaRef currentAudioVae = MediaRef.FromWGNodeData(generator.CurrentAudioVae, bridge);
        LtxChainCapture capture =
            TryCapture(bridge, currentMedia, currentAudioVae, useReusedAudio);
        if (capture is null)
        {
            return null;
        }

        LTXVSeparateAVLatentNode separate =
            bridge.Graph.GetNode<LTXVSeparateAVLatentNode>(capture.SeparateId);
        ComfyNode decode = bridge.Graph.GetNode(capture.DecodeId);
        JArray avLatentPath = separate?.AvLatent.Connection is not null
            ? WorkflowBridge.ToPath(separate.AvLatent.Connection)
            : null;
        JArray videoVaePath = decode?.FindInput("vae")?.Connection is INodeOutput vaeOutput
            ? WorkflowBridge.ToPath(vaeOutput)
            : null;
        JArray audioVaePath = capture.AudioVaeSource is not null
            ? WorkflowBridge.ToPath(capture.AudioVaeSource)
            : null;
        if (avLatentPath is null || videoVaePath is null || audioVaePath is null)
        {
            return null;
        }

        return new LtxPostVideoChainState(
            LtxStageInputArtifactFactory.CloneMedia(generator, generator.CurrentMedia),
            avLatentPath,
            new JArray(capture.SeparateId, 1),
            videoVaePath,
            audioVaePath,
            capture.DecodeId,
            capture.AudioDecodeId,
            new JArray(capture.DecodeId, 0),
            capture.HasPostDecodeWrappers,
            useReusedAudio);
    }

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

        IVaeDecode decode = mediaNode as IVaeDecode
            ?? bridge.Graph.FindNearestUpstream<IVaeDecode>(mediaNode);
        if (decode is null
            || decode.Samples.Connection?.Node is not LTXVSeparateAVLatentNode separate
            || separate.AvLatent.Connection is null
            || decode.Vae.Connection is null)
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

        return new LtxChainCapture(
            DecodeId: decode.Id,
            SeparateId: separate.Id,
            AudioDecodeId: audioDecode?.Id,
            AudioVaeSource: audioVaeSource,
            CurrentOutputMedia: currentMedia.Clone(),
            HasPostDecodeWrappers: !ReferenceEquals(currentMedia.Output.Node, decode),
            UseReusedAudio: useReusedAudio);
    }

    public static LtxChainCapture Rehydrate(
        LtxPostVideoChainState state,
        WorkflowBridge bridge)
    {
        INodeOutput audioVaeSource = state.AudioVaePath is JArray { Count: 2 } audioVaePath
            ? bridge.ResolvePath(audioVaePath)
            : null;

        return new LtxChainCapture(
            DecodeId: state.VideoDecodeNodeId,
            SeparateId: $"{state.AudioLatentPath[0]}",
            AudioDecodeId: state.AudioDecodeNodeId,
            AudioVaeSource: audioVaeSource,
            CurrentOutputMedia: MediaRef.FromWGNodeData(state.CurrentOutputMedia, bridge),
            HasPostDecodeWrappers: state.HasPostDecodeWrappers,
            UseReusedAudio: state.UseReusedAudioLatent);
    }
}
