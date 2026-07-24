using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Restores the native audio-latent route when LTX publication exposed that same route through a
/// decoder. Authored uploads and additive audio mixes are deliberately left as raw audio.
/// </summary>
internal static class LtxDecodedAudioHandoff
{
    internal static WGNodeData PreferNativeLatent(
        WorkflowGenerator generator,
        WGNodeData media)
    {
        WGNodeData decodedAudio = media?.AttachedAudio;
        if (!TryResolveNativeLatent(generator, decodedAudio, out JArray nativePath))
        {
            return media;
        }

        WGNodeData normalized = media.Duplicate();
        normalized.AttachedAudio = new WGNodeData(
            nativePath,
            generator,
            WGNodeData.DT_LATENT_AUDIO,
            generator.CurrentAudioVae?.Compat ?? decodedAudio.Compat)
        {
            Width = decodedAudio.Width,
            Height = decodedAudio.Height,
            Frames = decodedAudio.Frames,
            FPS = decodedAudio.FPS
        };
        return normalized;
    }

    internal static bool TryResolveNativeLatent(
        WorkflowGenerator generator,
        WGNodeData decodedAudio,
        out JArray nativePath)
    {
        nativePath = null;
        if (generator is null
            || decodedAudio?.DataType != WGNodeData.DT_AUDIO
            || decodedAudio.Path is not JArray { Count: 2 } audioPath)
        {
            return false;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        if (bridge.ResolvePath(audioPath)?.Node is not LTXVAudioVAEDecodeNode decode
            || decode.Samples.Connection is not INodeOutput nativeAudioLatent)
        {
            return false;
        }

        nativePath = WorkflowBridge.ToPath(nativeAudioLatent);
        return true;
    }
}
