using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Builds every requested boundary input or degrades the whole boundary to a cut.</summary>
internal sealed class BoundaryHandoffResolver(
    WorkflowGenerator g,
    ContinuityGuideBuilder continuityGuideBuilder)
{
    internal LtxBoundaryAudioCarry Resolve(
        TimelineBoundaries boundaries,
        ClipPlan previousClip,
        WGNodeData previousOutput,
        ClipPlan nextClip,
        ClipContext clipContext)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        ArgumentNullException.ThrowIfNull(previousClip);
        ArgumentNullException.ThrowIfNull(nextClip);
        ArgumentNullException.ThrowIfNull(clipContext);

        bool wantsContinuity = boundaries.TryGetContinueInput(
            previousClip.ClipId,
            out int continueHandleFrames,
            out int continuityWindow);
        bool wantsAudioCarry = boundaries.TryGetAudioCarryWindow(
            previousClip.ClipId,
            out int audioWindow);

        if (wantsContinuity)
        {
            if (!nextClip.Stages.Any(stage => !stage.IsPassthrough))
            {
                PlanDiagnosticReporter.TrackRequestWarning(
                    g.UserInput,
                    $"VideoStages: Clip {nextClip.ClipId} has no generating stage for its "
                    + "incoming Continue handle; treating the boundary as a cut.");
                boundaries.DegradeToCut(previousClip.ClipId);
                return null;
            }
            if (nextClip.Audio.Length.Owner != AudioLengthOwner.Timeline
                || nextClip.Frames is not int targetFrames
                || targetFrames <= 0
                || targetFrames > int.MaxValue - continueHandleFrames)
            {
                PlanDiagnosticReporter.TrackRequestWarning(
                    g.UserInput,
                    $"VideoStages: Clip {nextClip.ClipId} has a runtime-derived duration, so its "
                    + "incoming Continue handle cannot be added safely; treating the boundary as a cut.");
                boundaries.DegradeToCut(previousClip.ClipId);
                return null;
            }
            if (nextClip.EntryMode == ArchitectureEntryMode.InitVideo)
            {
                PlanDiagnosticReporter.TrackRequestWarning(
                    g.UserInput,
                    $"VideoStages: Clip {previousClip.ClipId} boundary 'continue' flows into "
                    + $"init-video Clip {nextClip.ClipId}; treating the boundary as a cut.");
                boundaries.DegradeToCut(previousClip.ClipId);
                return null;
            }

            clipContext.ContinuityFrame = continuityGuideBuilder.TryBuild(
                previousClip,
                previousOutput,
                continuityWindow,
                new TimelineGeometry(
                    clipContext.Dimensions.Width,
                    clipContext.Dimensions.Height,
                    clipContext.Plan.FramesPerSecond));
            if (clipContext.ContinuityFrame is null)
            {
                boundaries.DegradeToCut(previousClip.ClipId);
                return null;
            }
            clipContext.IncomingContinueHandleFrames = continueHandleFrames;
        }

        if (!wantsAudioCarry)
        {
            return null;
        }

        LtxBoundaryAudioCarry carry = TryBuildAudioCarry(
            previousClip,
            previousOutput,
            nextClip,
            audioWindow);
        if (carry is null)
        {
            // Without carry conditioning, trimming the overlap would shorten audio.
            clipContext.ContinuityFrame = null;
            clipContext.IncomingContinueHandleFrames = 0;
            boundaries.DegradeToCut(previousClip.ClipId);
        }
        return carry;
    }

    private LtxBoundaryAudioCarry TryBuildAudioCarry(
        ClipPlan previousClip,
        WGNodeData previousOutput,
        ClipPlan nextClip,
        int windowFrames)
    {
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
