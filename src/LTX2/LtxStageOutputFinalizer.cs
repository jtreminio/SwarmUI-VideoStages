using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal sealed class LtxStageOutputFinalizer(WorkflowGenerator g)
{
    private readonly GlobalVideoFrameTrimmer globalVideoFrameTrimmer = new(g);

    internal void Complete(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        LtxPostVideoChainCapture postVideoChain)
    {
        // Clip dimensions are updated by both pixel/model and latent upscalers. They are the
        // authoritative rendered dimensions; intermediate WGNodeData instances can still carry
        // the pre-upscale source metadata after the sampler swaps only their graph path.
        int outputWidth = stageFrame.ClipContext.Dimensions.Width;
        int outputHeight = stageFrame.ClipContext.Dimensions.Height;
        bool splicedIntoNativeChain = postVideoChain is not null;
        bool parallelMultiClip = stageFrame.ExecutionOptions.RequiresDedicatedOutput;
        if (splicedIntoNativeChain)
        {
            if (parallelMultiClip)
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

            if (!parallelMultiClip && postVideoChain.HasPostDecodeWrappers)
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

        bool shouldApplyTrim = stageFrame.Stage.Output.IsTimelineTerminal
            && globalVideoFrameTrimmer.IsRequested
            && !(splicedIntoNativeChain && postVideoChain.HasPostDecodeWrappers);
        if (shouldApplyTrim)
        {
            globalVideoFrameTrimmer.Apply();
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

        // MediaRef.FromWGNodeData pre-copies any existing AttachedAudio (possibly a latent
        // route) into the snapshot, so non-null cannot mean "freshly decoded" — only a
        // DT_AUDIO attachment proves AttachDecodedLtxAudio succeeded.
        if (currentMedia.AttachedAudio is MediaRef attachedAudio
            && attachedAudio.DataType == WGNodeData.DT_AUDIO)
        {
            g.CurrentMedia.AttachedAudio = attachedAudio.ToWGNodeData(g);
        }
        else if (g.CurrentMedia.AttachedAudio is WGNodeData latentAudio
            && latentAudio.DataType == WGNodeData.DT_LATENT_AUDIO
            && latentAudio.Path is JArray latentAudioPath)
        {
            // AsRawImage on a concat AV latent (e.g. the crop-guides output) reads the concat's
            // pre-join routes, so the video decode consumes the crop node — not a separate — and
            // the decode→separate shortcut above finds no audio. The stashed latent-audio route
            // is authoritative: decode it directly so no consumer sees a latent as save audio.
            LTXVAudioVAEDecodeNode audioDecode = bridge.AddNode(new LTXVAudioVAEDecodeNode());
            audioDecode.Samples.TryConnectFromPath(bridge, latentAudioPath);
            audioDecode.AudioVae.ConnectFrom(audioVae);
            bridge.SyncNode(audioDecode);
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
