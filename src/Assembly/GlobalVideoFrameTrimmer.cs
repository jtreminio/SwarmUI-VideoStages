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
    public bool IsRequested =>
        g.UserInput.Get(T2IParamTypes.TrimVideoStartFrames, 0) != 0
        || g.UserInput.Get(T2IParamTypes.TrimVideoEndFrames, 0) != 0;

    public void Apply()
    {
        if (!IsRequested)
        {
            return;
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        RuntimeArtifact current = RuntimeArtifact.Capture(
            g,
            bridge);
        // Capture nulls audio it cannot resolve, which is indistinguishable downstream from a
        // video that never had any, so a dropped stream would publish as a silently muted video.
        if (g.CurrentMedia?.AttachedAudio is not null
            && current.Media is { AttachedAudio: null })
        {
            throw Invariant.Failure(
                "the attached audio stream required for global frame trim "
                + "is unavailable in the workflow.");
        }
        Apply(current, bridge).PublishTo(g);
    }

    public RuntimeArtifact Apply(RuntimeArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!IsRequested)
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
        MediaRef attachedAudio = media.AttachedAudio;
        ValidateAttachedAudio(
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

        int keptFrames = TrimmedFrameCount(
            originalFrames,
            trimStartFrames,
            trimEndFrames) ?? 0;
        TrimAudioDurationNode audioTrim = bridge.AddNode(new TrimAudioDurationNode().With(
            StartIndex: Math.Max(0, trimStartFrames) / (double)framesPerSecond.Value,
            Duration: keptFrames / (double)framesPerSecond.Value));
        audioTrim.Audio.ConnectToUntyped(audio.Output);
        return new MediaRef
        {
            Output = audioTrim.AUDIO,
            DataType = WGNodeData.DT_AUDIO,
            Compat = audio.Compat,
        };
    }

    private static void ValidateAttachedAudio(
        MediaRef audio,
        int? originalFrames,
        int? framesPerSecond)
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
        if (originalFrames is not > 0 || framesPerSecond is not > 0)
        {
            throw Invariant.Failure(
                "the final video uses global frame trim, but its frame count or "
                + "frame rate is unavailable, so attached audio cannot be trimmed in sync.");
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
