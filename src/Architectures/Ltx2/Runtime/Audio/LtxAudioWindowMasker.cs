using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Authoring;
using VideoStages.Execution.Graph;
using VideoStages.Generated;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Applies the retake window to both channels of an AV latent. Mask value 1.0 regenerates content;
/// 0.0 preserves the encoded reference.
///
/// The stock node can only merge per-frame scalar video masks. Because
/// <see cref="LtxVideoRetakeMasker"/> supplies an H×W-resized mask, this node owns the video mask
/// and starts it at 0.0. Window times use the same latent-frame boundaries as the video mask.
/// </summary>
internal sealed class LtxAudioWindowMasker(WorkflowGenerator g)
{
    /// <summary>A single audio-latent noise window, in seconds along the clip timeline.</summary>
    internal readonly record struct AudioMaskWindow(double StartTime, double EndTime)
    {
        public bool IsEmpty => EndTime - StartTime <= 1e-6;
    }

    /// <summary>
    /// Retake window in seconds, snapped to the video mask's latent-frame boundaries.
    /// </summary>
    internal static AudioMaskWindow ComputeRetakeWindow(RetakeWindowSpec retake, int fps, int? clipFrames)
    {
        if (retake is null || retake.LengthFrames <= 0 || fps <= 0)
        {
            return default;
        }
        int pixelFrames = clipFrames is > 0
            ? clipFrames.Value
            : LtxStageRuntimeSettings.DefaultFrameCount;
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
    /// Applies the mask and returns false without changing the graph when a prerequisite is absent.
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

        int fps = genInfo.VideoFPS ?? LtxStageRuntimeSettings.DefaultFps;
        LTXVSetAudioVideoMaskByTimeNode mask = bridge.AddNode(
            new LTXVSetAudioVideoMaskByTimeNode().With(
                StartTime: window.StartTime,
                EndTime: window.EndTime,
                VideoFps: fps,
                MaskVideo: true,
                MaskAudio: true,
                // The stock node cannot merge the H×W retake mask, so it writes the window over
                // a preserved base.
                MaskInitValueVideo: 0.0,
                MaskInitValueAudio: 0.0),
            StableNodeIds.Id(g, StableNodeIds.AudioWindowMask, stageFrame.Stage.StageId));
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
        StagePlan stage = stageFrame.Stage;
        ClipPlan clip = stageFrame.ClipContext.PlannedClip
            ?? throw Invariant.Failure(
                "LTX stage execution requires the compiled clip plan.");
        int fps = genInfo.VideoFPS ?? LtxStageRuntimeSettings.DefaultFps;
        Ltx2StagePayload payload = stage.RequireLtx2Payload();

        // Match the video mask's frame-count preference so both windows cover the same span.
        if (payload.Retake is not null)
        {
            return ComputeRetakeWindow(
                new RetakeWindowSpec(
                    payload.Retake.StartFrame,
                    payload.Retake.LengthFrames,
                    payload.Retake.Strength),
                fps,
                genInfo.Frames ?? clip.Frames);
        }

        return default;
    }
}
