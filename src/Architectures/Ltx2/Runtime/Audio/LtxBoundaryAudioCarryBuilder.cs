using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Extracts the previous generated clip's decoded audio tail for use as the next clip's opening
/// generation context. Final timeline assembly never consumes this artifact directly.
/// </summary>
internal sealed class LtxBoundaryAudioCarryBuilder(WorkflowGenerator g)
{
    internal LtxBoundaryAudioCarry TryBuild(
        ClipPlan previousClip,
        WGNodeData previousOutput,
        ClipPlan nextClip,
        int windowFrames)
    {
        ArgumentNullException.ThrowIfNull(previousClip);
        ArgumentNullException.ThrowIfNull(nextClip);
        if (windowFrames <= 0
            || previousOutput?.AttachedAudio?.Path is not JArray previousAudioPath
            || previousOutput.Frames is not int previousFrames
            || previousOutput.GetRawFPS() is not int fps
            || fps <= 0
            || windowFrames > previousFrames)
        {
            Logs.Warning(
                $"VideoStages: Clip {previousClip.ClipId} cannot carry audio into "
                + $"Clip {nextClip.ClipId} because its decoded audio timing is unavailable.");
            return null;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput previousAudio = bridge.ResolvePath(previousAudioPath);
        if (previousAudio is null)
        {
            Logs.Warning(
                $"VideoStages: Clip {previousClip.ClipId} cannot carry audio into "
                + $"Clip {nextClip.ClipId} because its decoded audio output cannot be resolved.");
            return null;
        }

        double durationSeconds = windowFrames / (double)fps;
        TrimAudioDurationNode tail = bridge.AddNode(new TrimAudioDurationNode()).With(
            StartIndex: (previousFrames - windowFrames) / (double)fps,
            Duration: durationSeconds);
        tail.Audio.ConnectToUntyped(previousAudio);

        return new(
            new WGNodeData(
                WorkflowBridge.ToPath(tail.AUDIO),
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae?.Compat ?? previousOutput.AttachedAudio.Compat),
            durationSeconds);
    }
}

internal sealed record LtxBoundaryAudioCarry(
    WGNodeData Tail,
    double DurationSeconds);
