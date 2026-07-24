using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>Applies the host's global frame trim to the authoritative video output.</summary>
internal sealed class GlobalVideoFrameTrimmer(WorkflowGenerator g)
{
    public bool IsRequested =>
        g.UserInput.Get(T2IParamTypes.TrimVideoStartFrames, 0) != 0
        || g.UserInput.Get(T2IParamTypes.TrimVideoEndFrames, 0) != 0;

    public void Apply()
    {
        int trimStartFrames = g.UserInput.Get(T2IParamTypes.TrimVideoStartFrames, 0);
        int trimEndFrames = g.UserInput.Get(T2IParamTypes.TrimVideoEndFrames, 0);
        if (trimStartFrames == 0 && trimEndFrames == 0)
        {
            return;
        }
        // A requested trim is never dropped: an unusable output is the same failure as the
        // non-video output rejected below, and silently publishing untrimmed video hides it.
        if (g.CurrentMedia?.Path is not JArray { Count: 2 } currentPath)
        {
            throw new SwarmUserErrorException(
                "VideoStages: the final output uses global frame trim, but the timeline produced "
                + "no decoded output to trim.");
        }
        if (g.CurrentMedia.DataType != WGNodeData.DT_VIDEO)
        {
            throw new SwarmUserErrorException(
                "VideoStages: the final output uses global frame trim, but it is not a decoded "
                + "video stream.");
        }
        int? originalFrames = g.CurrentMedia.Frames;
        int? framesPerSecond = g.CurrentMedia.GetRawFPS();
        WGNodeData attachedAudio = g.CurrentMedia.AttachedAudio;
        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput videoOutput = ResolveDecodedOutput(
            bridge,
            currentPath,
            "video");
        INodeOutput audioOutput = ValidateAttachedAudio(
            bridge,
            attachedAudio,
            originalFrames,
            framesPerSecond);
        SwarmTrimFramesNode trim = bridge.AddNode(new SwarmTrimFramesNode().With(
            TrimStart: trimStartFrames,
            TrimEnd: trimEndFrames));
        trim.Image.ConnectToUntyped(videoOutput);

        int? frames = TrimmedFrameCount(
            originalFrames,
            trimStartFrames,
            trimEndFrames);
        g.CurrentMedia = g.CurrentMedia.WithPath(trim.IMAGE.ToPath());
        g.CurrentMedia.AttachedAudio = TrimAttachedAudio(
            bridge,
            attachedAudio,
            audioOutput,
            originalFrames,
            framesPerSecond,
            trimStartFrames,
            trimEndFrames);
        g.CurrentMedia.Frames = frames ?? g.CurrentMedia.Frames;
    }

    private WGNodeData TrimAttachedAudio(
        WorkflowBridge bridge,
        WGNodeData audio,
        INodeOutput audioOutput,
        int? originalFrames,
        int? framesPerSecond,
        int trimStartFrames,
        int trimEndFrames)
    {
        if (audio is null)
        {
            return null;
        }

        int keptFrames = TrimmedFrameCount(
            originalFrames,
            trimStartFrames,
            trimEndFrames) ?? 0;
        TrimAudioDurationNode audioTrim = bridge.AddNode(new TrimAudioDurationNode().With(
            StartIndex: Math.Max(0, trimStartFrames) / (double)framesPerSecond.Value,
            Duration: keptFrames / (double)framesPerSecond.Value));
        audioTrim.Audio.ConnectToUntyped(audioOutput);
        return new WGNodeData(
            audioTrim.AUDIO.ToPath(),
            g,
            WGNodeData.DT_AUDIO,
            audio.Compat);
    }

    private static INodeOutput ValidateAttachedAudio(
        WorkflowBridge bridge,
        WGNodeData audio,
        int? originalFrames,
        int? framesPerSecond)
    {
        if (audio is null)
        {
            return null;
        }
        if (audio.DataType != WGNodeData.DT_AUDIO
            || audio.Path is not JArray { Count: 2 } audioPath)
        {
            throw new SwarmUserErrorException(
                "VideoStages: the final video uses global frame trim, but its attached audio "
                + "is not a decoded audio stream.");
        }
        if (originalFrames is not > 0 || framesPerSecond is not > 0)
        {
            throw new SwarmUserErrorException(
                "VideoStages: the final video uses global frame trim, but its frame count or "
                + "frame rate is unavailable, so attached audio cannot be trimmed in sync.");
        }
        return ResolveDecodedOutput(bridge, audioPath, "attached audio");
    }

    private static INodeOutput ResolveDecodedOutput(
        WorkflowBridge bridge,
        JArray path,
        string outputKind) =>
        bridge.ResolvePath(path)
        ?? throw new SwarmUserErrorException(
            $"VideoStages: the final {outputKind} stream required for global frame trim "
            + "is unavailable in the workflow.");

    private static int? TrimmedFrameCount(int? frames, int trimStart, int trimEnd)
    {
        if (frames is null)
        {
            return null;
        }

        int removedFrames = Math.Max(0, trimStart) + Math.Max(0, trimEnd);
        return Math.Max(0, frames.Value - removedFrames);
    }
}
