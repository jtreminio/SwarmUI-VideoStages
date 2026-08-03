using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Captures the previous clip's audio sources for boundary carry.</summary>
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
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: Clip {previousClip.ClipId} cannot carry audio into "
                + $"Clip {nextClip.ClipId} because its decoded audio timing is unavailable; "
                + "treating the boundary as a cut.");
            return null;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        if (bridge.ResolvePath(previousAudioPath) is null)
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                g.UserInput,
                $"VideoStages: Clip {previousClip.ClipId} cannot carry audio into "
                + $"Clip {nextClip.ClipId} because its decoded audio output cannot be resolved; "
                + "treating the boundary as a cut.");
            return null;
        }

        double durationSeconds = windowFrames / (double)fps;
        double sourceStartSeconds = (previousFrames - windowFrames) / (double)fps;
        WGNodeData nativeLatent = null;
        if (LtxDecodedAudioHandoff.TryResolveNativeLatent(
                g,
                previousOutput.AttachedAudio,
                out JArray nativeAudioPath))
        {
            nativeLatent = new WGNodeData(
                nativeAudioPath,
                g,
                WGNodeData.DT_LATENT_AUDIO,
                g.CurrentAudioVae?.Compat ?? previousOutput.AttachedAudio.Compat);
        }
        return new(
            previousOutput.AttachedAudio,
            durationSeconds,
            sourceStartSeconds,
            nativeLatent);
    }
}

internal sealed record LtxBoundaryAudioCarry(
    WGNodeData DecodedSource,
    double DurationSeconds,
    double SourceStartSeconds = 0,
    WGNodeData NativeLatent = null);
