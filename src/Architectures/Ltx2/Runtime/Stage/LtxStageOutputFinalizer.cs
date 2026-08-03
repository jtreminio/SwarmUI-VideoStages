using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxStageOutputFinalizer(WorkflowGenerator g)
{
    internal void Complete(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        LtxPostVideoChainCapture postVideoChain,
        bool requiresDedicatedOutput)
    {
        // WGNodeData can retain pre-upscale metadata when a sampler replaces only its graph path.
        int outputWidth = stageFrame.ClipContext.Dimensions.Width;
        int outputHeight = stageFrame.ClipContext.Dimensions.Height;
        bool splicedIntoNativeChain = postVideoChain is not null;
        if (splicedIntoNativeChain)
        {
            if (requiresDedicatedOutput)
            {
                LtxPostVideoChainSplicer.SpliceCurrentOutputToDedicatedBranch(
                    postVideoChain,
                    g,
                    genInfo.Vae,
                    outputWidth,
                    outputHeight,
                    genInfo.Frames,
                    genInfo.VideoFPS);
            }
            else
            {
                LtxPostVideoChainSplicer.SpliceCurrentOutput(postVideoChain, g, genInfo.Vae);
            }

            if (!requiresDedicatedOutput && postVideoChain.HasPostDecodeWrappers)
            {
                ApplyCurrentMediaOutputMetadata(
                    outputWidth,
                    outputHeight,
                    postVideoChain.CurrentOutputMedia.Frames,
                    postVideoChain.CurrentOutputMedia.GetRawFPS());
            }
            else
            {
                ApplyCurrentMediaOutputMetadata(
                    outputWidth,
                    outputHeight,
                    genInfo.Frames,
                    genInfo.VideoFPS);
            }
            AttachDecodedLtxAudioFromCurrentVideo();
        }
        else
        {
            g.CurrentMedia = VaeDecodePreference.AsRawImage(g, g.CurrentMedia, genInfo.Vae);
            AttachDecodedLtxAudioFromCurrentVideo();
            ApplyCurrentMediaOutputMetadata(
                outputWidth,
                outputHeight,
                genInfo.Frames,
                genInfo.VideoFPS);
        }

        g.CurrentVae = genInfo.Vae;
    }

    private void AttachDecodedLtxAudioFromCurrentVideo()
    {
        if (g.CurrentMedia?.Path is not JArray { Count: 2 } currentPath)
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        MediaRef currentMedia = MediaRef.FromWGNodeData(g.CurrentMedia, bridge);
        if (currentMedia is null)
        {
            return;
        }

        MediaRef audioVae = ResolveAudioVaeMediaRef(bridge);
        if (audioVae is null)
        {
            return;
        }

        LtxPostChainRebuilder.AttachDecodedLtxAudio(bridge, currentMedia, audioVae);

        // FromWGNodeData preserves latent attachments, so require decoded audio explicitly.
        if (currentMedia.AttachedAudio is MediaRef attachedAudio
            && attachedAudio.DataType == WGNodeData.DT_AUDIO)
        {
            g.CurrentMedia.AttachedAudio = attachedAudio.ToWGNodeData(g);
        }
        else if (g.CurrentMedia.AttachedAudio is WGNodeData latentAudio
            && latentAudio.DataType == WGNodeData.DT_LATENT_AUDIO
            && latentAudio.Path is JArray latentAudioPath)
        {
            // Concat AV latents can bypass separation, so decode the stashed audio latent.
            LTXVAudioVAEDecodeNode audioDecode = bridge.AddNode(new LTXVAudioVAEDecodeNode());
            audioDecode.Samples.TryConnectFromPath(bridge, latentAudioPath);
            audioDecode.AudioVae.ConnectFrom(audioVae);
            g.CurrentMedia.AttachedAudio = new WGNodeData(
                audioDecode.Audio.ToPath(),
                g,
                WGNodeData.DT_AUDIO,
                audioVae.Compat);
        }
    }

    private MediaRef ResolveAudioVaeMediaRef(WorkflowBridge bridge)
    {
        MediaRef audioVae = MediaRef.FromWGNodeData(g.CurrentAudioVae, bridge);
        if (audioVae is not null)
        {
            return audioVae;
        }

        LTXVAudioVAEDecodeNode existingAudioDecode = bridge.Graph
            .NodesOfType<LTXVAudioVAEDecodeNode>()
            .FirstOrDefault();
        if (existingAudioDecode?.AudioVae.Connection is not INodeOutput audioVaeOutput)
        {
            return null;
        }

        return new MediaRef
        {
            Output = audioVaeOutput,
            DataType = WGNodeData.DT_AUDIO,
            Compat = g.CurrentAudioVae?.Compat
        };
    }

    private void ApplyCurrentMediaOutputMetadata(
        int width,
        int height,
        int? frames,
        int? fps)
    {
        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
        g.CurrentMedia.Frames = frames ?? g.CurrentMedia.Frames;
        g.CurrentMedia.FPS = fps ?? g.CurrentMedia.FPS;
    }
}
