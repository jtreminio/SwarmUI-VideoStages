using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;

namespace VideoStages.LTX2;

/// <summary>
/// Windows BOTH channels of a combined av-latent to the retake span — video and audio are
/// (re)generated inside the window, locked to the encoded reference outside — via the stock
/// <see cref="LTXVSetAudioVideoMaskByTimeNode"/> (mask value 1.0 regenerates, 0.0 preserves).
///
/// The node must own the video mask too (<c>mask_video=true, init=0.0</c>): its merge of a
/// pre-existing video mask only fires for per-frame scalar masks (1,1,F,1,1), and the mask
/// <see cref="LtxVideoRetakeMasker"/> attaches is H×W-resized, so it silently degenerates to the
/// init value — 1.0 would regenerate the entire video. The window seconds are snapped to the same
/// latent-frame blocks as the video retake mask so both mechanisms describe identical frames.
/// </summary>
internal sealed class LtxAudioWindowMasker(WorkflowGenerator g)
{
    private const int AudioWindowIdBase = 52400;

    /// <summary>A single audio-latent noise window, in seconds along the clip timeline.</summary>
    internal readonly record struct AudioMaskWindow(double StartTime, double EndTime)
    {
        public bool IsEmpty => EndTime - StartTime <= 1e-6;
    }

    /// <summary>
    /// Retake window in seconds, snapped to latent-frame boundaries: the mask-by-time node
    /// searchsorts these times against the latent pixel grid, so the snapped values select exactly
    /// the latent frames <see cref="LtxVideoRetakeMasker.ComputeLatentWindow"/> regenerates.
    /// </summary>
    internal static AudioMaskWindow ComputeRetakeWindow(RetakeWindowSpec retake, int fps, int? clipFrames)
    {
        if (retake is null || retake.LengthFrames <= 0 || fps <= 0)
        {
            return default;
        }
        int pixelFrames = clipFrames is > 0 ? clipFrames.Value : LtxStageExecutor.DefaultFrameCountValue;
        LtxVideoRetakeMasker.LatentWindow blocks =
            LtxVideoRetakeMasker.ComputeLatentWindow(pixelFrames, retake.StartFrame, retake.LengthFrames);
        if (blocks.Window <= 0)
        {
            return default;
        }
        int startPixel = LtxVideoRetakeMasker.LatentFrameStartPixel(blocks.Prefix);
        int endPixel = LtxVideoRetakeMasker.LatentFrameStartPixel(blocks.Prefix + blocks.Window);
        return new AudioMaskWindow((double)startPixel / fps, (double)endPixel / fps);
    }

    /// <summary>
    /// Splices the time-windowed audio+video noise mask onto the current av-latent + conditioning;
    /// returns true when applied, false (graph unchanged) when a precondition fails (not LTXV2, no audio
    /// VAE, the latent carries no audio, or the resolved window is empty).
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
                MaskVideo: true,
                MaskAudio: true,
                // The node overwrites the video mask (the H×W retake mask can't be merged in, see class
                // doc), so it must write the window itself over a frozen (0.0) base.
                MaskInitValueVideo: 0.0,
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
