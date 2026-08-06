using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution;

namespace VideoStages;

internal sealed class GlobalVideoFrameTrimmer(WorkflowGenerator g)
{
    internal static bool IsRequested(T2IParamInput input) =>
        input.Get(T2IParamTypes.TrimVideoStartFrames, 0) != 0
        || input.Get(T2IParamTypes.TrimVideoEndFrames, 0) != 0;

    public RuntimeArtifact Apply(RuntimeArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!IsRequested(g.UserInput))
        {
            return artifact;
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        return Apply(artifact, bridge);
    }

    private RuntimeArtifact Apply(RuntimeArtifact artifact, WorkflowBridge bridge)
    {
        int trimStartFrames = g.UserInput.Get(T2IParamTypes.TrimVideoStartFrames, 0);
        int trimEndFrames = g.UserInput.Get(T2IParamTypes.TrimVideoEndFrames, 0);
        MediaRef media = artifact.Media;
        // Fail closed because publishing untrimmed video would hide an unusable output.
        if (media?.Output is not INodeOutput videoOutput)
        {
            throw Invariant.Failure(
                "the final output uses global frame trim, but the timeline produced "
                + "no decoded output to trim.");
        }
        if (media.DataType != WGNodeData.DT_VIDEO)
        {
            throw Invariant.Failure(
                "the final output uses global frame trim, but it is not a decoded "
                + "video stream.");
        }
        int? originalFrames = media.Frames;
        int? framesPerSecond = media.FPS?.Type == JTokenType.Integer
            ? media.FPS.Value<int>()
            : null;
        if (originalFrames is int knownFrames
            && Math.Max(0, trimStartFrames) + Math.Max(0, trimEndFrames) >= knownFrames)
        {
            throw Invariant.Failure(
                $"global frame trim removes all {knownFrames} frames of the final video.");
        }
        MediaRef attachedAudio = media.AttachedAudio;
        ValidateAttachedAudio(attachedAudio, framesPerSecond);
        SwarmTrimFramesNode trim = bridge.AddNode(new SwarmTrimFramesNode().With(
            TrimStart: trimStartFrames,
            TrimEnd: trimEndFrames));
        trim.Image.ConnectToUntyped(videoOutput);

        int? frames = TrimmedFrameCount(
            originalFrames,
            trimStartFrames,
            trimEndFrames);
        return artifact with
        {
            Media = new MediaRef
            {
                Output = trim.IMAGE,
                DataType = media.DataType,
                Compat = media.Compat,
                Width = media.Width,
                Height = media.Height,
                Frames = frames ?? media.Frames,
                FPS = media.FPS,
                AttachedAudio = TrimAttachedAudio(
                    bridge,
                    attachedAudio,
                    originalFrames,
                    framesPerSecond,
                    trimStartFrames,
                    trimEndFrames),
            },
        };
    }

    private MediaRef TrimAttachedAudio(
        WorkflowBridge bridge,
        MediaRef audio,
        int? originalFrames,
        int? framesPerSecond,
        int trimStartFrames,
        int trimEndFrames)
    {
        if (audio is null)
        {
            return null;
        }

        int? keptFrames = TrimmedFrameCount(
            originalFrames,
            trimStartFrames,
            trimEndFrames);
        TrimAudioDurationNode audioTrim = bridge.AddNode(new TrimAudioDurationNode().With(
            StartIndex: Math.Max(0, trimStartFrames) / (double)framesPerSecond.Value,
            Duration: keptFrames is int kept
                ? kept / (double)framesPerSecond.Value
                : 86400));
        audioTrim.Audio.ConnectToUntyped(audio.Output);
        return new MediaRef
        {
            Output = audioTrim.AUDIO,
            DataType = WGNodeData.DT_AUDIO,
            Compat = audio.Compat,
        };
    }

    private static void ValidateAttachedAudio(MediaRef audio, int? framesPerSecond)
    {
        if (audio is null)
        {
            return;
        }
        if (audio.DataType != WGNodeData.DT_AUDIO
            || audio.Output is null)
        {
            throw Invariant.Failure(
                "the final video uses global frame trim, but its attached audio "
                + "is not a decoded audio stream.");
        }
        if (framesPerSecond is not > 0)
        {
            throw Invariant.Failure(
                "the final video uses global frame trim, but its frame rate is "
                + "unavailable, so attached audio cannot be trimmed in sync.");
        }
    }

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
