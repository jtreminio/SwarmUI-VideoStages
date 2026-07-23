using SwarmUI.Utils;
using VideoStages.LTX2;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>Applies a compiled ControlNet-owned duration to active LTX stage sources.</summary>
internal sealed class ControlNetClipLengthApplicator(LtxManager ltxManager)
{
    public void Apply(ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Audio.Length.Owner != AudioLengthOwner.ControlNet
            || clip.Stages.Count == 0)
        {
            return;
        }

        int sourceIndex = clip.Audio.Length.ControlNetSourceIndex
            ?? throw new SwarmUserErrorException(
                "VideoStages: ControlNet owns clip length, but the compiled plan has no valid "
                + "ControlNet 1-3 source.");
        if (!ltxManager.TryApplyControlNetFrameCount(sourceIndex))
        {
            throw new SwarmUserErrorException(
                $"VideoStages: ControlNet {sourceIndex + 1} owns clip {clip.ClipId} length, "
                + "but its captured video frame count is unavailable.");
        }
    }
}
