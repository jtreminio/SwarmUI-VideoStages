using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;

namespace VideoStages.LTX2;

/// <summary>
/// Windows the AUDIO channel of a combined av-latent to <c>[StartTime, EndTime]</c> seconds — audio is
/// (re)generated inside the window, locked to the encoded reference outside. Pairs with the video-only
/// <see cref="LtxVideoRetakeMasker"/> so a retake also regenerates the audio under the redone frames
/// while preserved frames keep their original audio.
///
/// Uses the stock <see cref="LTXVSetAudioVideoMaskByTimeNode"/>, which writes BOTH masks (1.0
/// regenerates, 0.0 preserves). Video is left at <c>mask_video=false, init=1.0</c>: the node multiplies
/// any pre-existing video mask INTO it, so 1.0 is the identity — a retake's video mask survives and a
/// plain generation still regenerates all video. (Init 0.0 would zero the mask and freeze the video.)
/// </summary>
internal sealed class LtxAudioWindowMasker(WorkflowGenerator g)
{
    private const int AudioWindowIdBase = 52400;

    /// <summary>A single audio-latent noise window, in seconds along the clip timeline.</summary>
    internal readonly record struct AudioMaskWindow(double StartTime, double EndTime)
    {
        public bool IsEmpty => EndTime - StartTime <= 1e-6;
    }

    /// <summary>Retake window in seconds: regenerate audio inside the retake pixel-frame span, preserve outside.</summary>
    internal static AudioMaskWindow ComputeRetakeWindow(RetakeWindowSpec retake, int fps, int? clipFrames)
    {
        if (retake is null || retake.LengthFrames <= 0 || fps <= 0)
        {
            return default;
        }
        int limit = clipFrames is > 0 ? clipFrames.Value : int.MaxValue;
        (int start, int end) = RetakeWindowSpec.ClampFrameWindow(retake.StartFrame, retake.LengthFrames, limit);
        return new AudioMaskWindow((double)start / fps, (double)end / fps);
    }

    /// <summary>
    /// Splices an audio-only time-windowed noise mask onto the current av-latent + conditioning; returns
    /// true when applied, false (graph unchanged) when a precondition fails (not LTXV2, no audio VAE, the
    /// latent carries no audio, or the resolved window is empty).
    /// </summary>
    public bool Apply(WorkflowGenerator.ImageToVideoGenInfo genInfo, StageFrame stageFrame)
    {
        if (!g.IsLTXV2() || g.CurrentAudioVae?.Path is null || genInfo?.Model?.Path is null || genInfo.Vae?.Path is null)
        {
            return false;
        }
        if (g.CurrentMedia?.Path is not JArray mediaPath)
        {
            return false;
        }

        AudioMaskWindow window = ResolveWindow(genInfo, stageFrame);
        if (window.IsEmpty)
        {
            return false;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        bool hasAudioLatent = g.CurrentMedia.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO
            || bridge.ResolvePath(mediaPath)?.Node is LTXVConcatAVLatentNode;
        if (!hasAudioLatent)
        {
            return false;
        }

        int fps = genInfo.VideoFPS ?? LtxStageExecutor.DefaultFpsValue;
        LTXVSetAudioVideoMaskByTimeNode mask = bridge.AddNode(
            new LTXVSetAudioVideoMaskByTimeNode().With(
                StartTime: window.StartTime,
                EndTime: window.EndTime,
                VideoFps: fps,
                MaskVideo: false,
                MaskAudio: true,
                // 1.0 = identity: the node multiplies any pre-existing video mask in; 0.0 would freeze all video.
                MaskInitValueVideo: 1.0,
                MaskInitValueAudio: 0.0),
            g.GetStableDynamicID(AudioWindowIdBase, stageFrame.SectionId));
        mask.AvLatentInput.ConnectFromPath(bridge, mediaPath);
        mask.Model.ConnectFromPath(bridge, genInfo.Model.Path);
        mask.Vae.ConnectFromPath(bridge, genInfo.Vae.Path);
        mask.AudioVae.ConnectFromPath(bridge, g.CurrentAudioVae.Path);
        if (genInfo.PosCond is not null)
        {
            mask.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond);
        }
        if (genInfo.NegCond is not null)
        {
            mask.NegativeInput.ConnectFromPath(bridge, genInfo.NegCond);
        }
        bridge.SyncNode(mask);

        // The node rewrites conditioning alongside the av-latent; thread its outputs to the sampler.
        genInfo.PosCond = mask.Positive.ToPath();
        genInfo.NegCond = mask.Negative.ToPath();
        g.CurrentMedia = g.CurrentMedia.WithPath(
            mask.AvLatent,
            WGNodeData.DT_LATENT_AUDIOVIDEO,
            genInfo.Model.Compat);
        return true;
    }

    private static AudioMaskWindow ResolveWindow(WorkflowGenerator.ImageToVideoGenInfo genInfo, StageFrame stageFrame)
    {
        StageSpec stage = stageFrame.Stage;
        ClipSpec clip = stageFrame.ClipContext.Clip;
        int fps = genInfo.VideoFPS ?? LtxStageExecutor.DefaultFpsValue;

        // Retake: preserved-frame audio stays locked to the base encoding. Matches the video retake mask's
        // frame-count preference (genInfo.Frames first) so both windows describe the same span.
        if (stage.RetakeWindow is not null && VideoStageModelCompat.IsLtxV2VideoModel(stage.Model))
        {
            return ComputeRetakeWindow(stage.RetakeWindow, fps, genInfo.Frames ?? clip.Frames);
        }

        return default;
    }
}
