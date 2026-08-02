using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Builds runtime inputs for a non-cut boundary. If a requested input cannot be built, the boundary
/// degrades to a cut so assembly does not trim an unconditioned overlap.
/// </summary>
internal sealed class BoundaryHandoffResolver(
    ContinuityGuideBuilder continuityGuideBuilder,
    LtxBoundaryAudioCarryBuilder audioCarryBuilder)
{
    internal LtxBoundaryAudioCarry Resolve(
        TimelineAssemblySession assembly,
        ClipPlan previousClip,
        WGNodeData previousOutput,
        ClipPlan nextClip,
        bool nextClipHasInitVideo,
        ClipContext clipContext)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(previousClip);
        ArgumentNullException.ThrowIfNull(nextClip);
        ArgumentNullException.ThrowIfNull(clipContext);

        bool wantsContinuity = assembly.TryGetContinueInput(
            previousClip.ClipId,
            out int continueHandleFrames,
            out int continuityWindow);
        bool wantsAudioCarry = assembly.TryGetAudioCarryWindow(
            previousClip.ClipId,
            out int audioWindow);

        if (wantsContinuity)
        {
            if (!nextClip.Stages.Any(stage => !stage.IsPassthrough))
            {
                assembly.ReportWarning(
                    $"VideoStages: Clip {nextClip.ClipId} has no generating stage for its "
                    + "incoming Continue handle; treating the boundary as a cut.");
                assembly.DegradeToCut(previousClip.ClipId);
                return null;
            }
            if (nextClip.Audio.Length.Owner != AudioLengthOwner.Timeline
                || nextClip.Frames is not int targetFrames
                || targetFrames <= 0
                || targetFrames > int.MaxValue - continueHandleFrames)
            {
                assembly.ReportWarning(
                    $"VideoStages: Clip {nextClip.ClipId} has a runtime-derived duration, so its "
                    + "incoming Continue handle cannot be added safely; treating the boundary as a cut.");
                assembly.DegradeToCut(previousClip.ClipId);
                return null;
            }
            if (nextClipHasInitVideo)
            {
                assembly.ReportWarning(
                    $"VideoStages: Clip {previousClip.ClipId} boundary 'continue' flows into "
                    + $"init-video Clip {nextClip.ClipId}; treating the boundary as a cut.");
                assembly.DegradeToCut(previousClip.ClipId);
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
                assembly.DegradeToCut(previousClip.ClipId);
                return null;
            }
            clipContext.IncomingContinueHandleFrames = continueHandleFrames;
        }

        if (!wantsAudioCarry)
        {
            return null;
        }

        LtxBoundaryAudioCarry carry = audioCarryBuilder.TryBuild(
            previousClip,
            previousOutput,
            nextClip,
            audioWindow);
        if (carry is null)
        {
            // Without carry conditioning, trimming the overlap would shorten audio.
            clipContext.ContinuityFrame = null;
            clipContext.IncomingContinueHandleFrames = 0;
            assembly.DegradeToCut(previousClip.ClipId);
        }
        return carry;
    }
}
