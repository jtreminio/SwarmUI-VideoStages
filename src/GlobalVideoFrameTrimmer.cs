using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution;

namespace VideoStages;

/// <summary>Applies the host's global frame trim to the authoritative video output.</summary>
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
        ValidateHostInput(bridge);
        RuntimeArtifact current = RuntimeArtifact.Capture(
            g,
            bridge);
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
            throw VideoStagesInvariant.Failure(
                "VideoStages: the final output uses global frame trim, but the timeline produced "
                + "no decoded output to trim.");
        }
        if (media.DataType != WGNodeData.DT_VIDEO)
        {
            throw VideoStagesInvariant.Failure(
                "VideoStages: the final output uses global frame trim, but it is not a decoded "
                + "video stream.");
        }
        bool videoAlreadyTrimmed = media.Output.Node is ComfyNode existingVideoTrim
            && existingVideoTrim.ClassTypeName == SwarmTrimFramesNode.ClassType
            && existingVideoTrim.FindInput("trim_start").LiteralAsInt() == trimStartFrames
            && existingVideoTrim.FindInput("trim_end").LiteralAsInt() == trimEndFrames;
        int removedFrames = Math.Max(0, trimStartFrames) + Math.Max(0, trimEndFrames);
        int? originalFrames = videoAlreadyTrimmed && media.Frames is int trimmedFrames
            ? trimmedFrames + removedFrames
            : media.Frames;
        int? framesPerSecond = media.FPS?.Type == JTokenType.Integer
            ? media.FPS.Value<int>()
            : null;
        MediaRef attachedAudio = media.AttachedAudio;
        ValidateAttachedAudio(
            attachedAudio,
            originalFrames,
            framesPerSecond);
        if (videoAlreadyTrimmed)
        {
            if (attachedAudio is null)
            {
                return artifact;
            }
            int keptFrames = TrimmedFrameCount(
                originalFrames,
                trimStartFrames,
                trimEndFrames) ?? 0;
            double audioStart = Math.Max(0, trimStartFrames)
                / (double)framesPerSecond.Value;
            double audioDuration = keptFrames
                / (double)framesPerSecond.Value;
            if (HasEquivalentAudioTrim(attachedAudio, audioStart, audioDuration))
            {
                return artifact;
            }
            return artifact with
            {
                Media = new MediaRef
                {
                    Output = media.Output,
                    DataType = media.DataType,
                    Compat = media.Compat,
                    Width = media.Width,
                    Height = media.Height,
                    Frames = media.Frames,
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

    /// <summary>
    /// Validates attached host audio before conversion because an unresolved path would otherwise
    /// be indistinguishable from intentionally absent audio.
    /// </summary>
    private void ValidateHostInput(WorkflowBridge bridge)
    {
        WGNodeData audio = g.CurrentMedia?.AttachedAudio;
        if (audio is null)
        {
            return;
        }
        if (audio.DataType != WGNodeData.DT_AUDIO
            || audio.Path is not JArray { Count: 2 } audioPath)
        {
            throw VideoStagesInvariant.Failure(
                "VideoStages: the final video uses global frame trim, but its attached audio "
                + "is not a decoded audio stream.");
        }
        if (g.CurrentMedia.Frames is not > 0
            || g.CurrentMedia.GetRawFPS() is not > 0)
        {
            throw VideoStagesInvariant.Failure(
                "VideoStages: the final video uses global frame trim, but its frame count or "
                + "frame rate is unavailable, so attached audio cannot be trimmed in sync.");
        }
        ResolveHostOutput(bridge, audioPath, "attached audio");
    }

    private static INodeOutput ResolveHostOutput(
        WorkflowBridge bridge,
        JArray path,
        string outputKind) =>
        bridge.ResolvePath(path)
        ?? throw VideoStagesInvariant.Failure(
            $"VideoStages: the final {outputKind} stream required for global frame trim "
            + "is unavailable in the workflow.");

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
            throw VideoStagesInvariant.Failure(
                "VideoStages: the final video uses global frame trim, but its attached audio "
                + "is not a decoded audio stream.");
        }
        if (originalFrames is not > 0 || framesPerSecond is not > 0)
        {
            throw VideoStagesInvariant.Failure(
                "VideoStages: the final video uses global frame trim, but its frame count or "
                + "frame rate is unavailable, so attached audio cannot be trimmed in sync.");
        }
    }

    private static bool HasEquivalentAudioTrim(
        MediaRef audio,
        double expectedStart,
        double expectedDuration) =>
        audio.Output?.Node is ComfyNode node
        && node.ClassTypeName == TrimAudioDurationNode.ClassType
        && node.FindInput("start_index").LiteralAsDouble() is double start
        && node.FindInput("duration").LiteralAsDouble() is double duration
        && Math.Abs(start - expectedStart) < 1e-6
        && Math.Abs(duration - expectedDuration) < 1e-6;

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
