using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Resolves every runtime artifact a non-cut boundary requires before the next clip runs. A
/// boundary is all-or-nothing: if any requested continuity or audio-carry input cannot be built,
/// the whole boundary degrades to a cut, so timeline assembly never trims an overlap whose
/// conditioning was never applied.
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

        bool wantsContinuity = assembly.TryGetContinueWindow(
            previousClip.ClipId,
            out int continuityWindow);
        bool wantsAudioCarry = assembly.TryGetAudioCarryWindow(
            previousClip.ClipId,
            out int audioWindow);

        if (wantsContinuity)
        {
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
                nextClip,
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
            // The carry conditioning is part of this boundary's contract; without it the merge
            // would still trim the overlap, shortening audio for an overlap nothing produced.
            clipContext.ContinuityFrame = null;
            assembly.DegradeToCut(previousClip.ClipId);
        }
        return carry;
    }
}
