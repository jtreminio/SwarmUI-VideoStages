using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Applies a compiled ControlNet-owned duration to active LTX stage sources.</summary>
internal sealed class ControlNetClipLengthApplicator(WorkflowGenerator g)
{
    public void Apply(ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Audio.Length.Owner != AudioLengthOwner.ControlNet
            || clip.Stages.Count == 0)
        {
            return;
        }

        int sourceIndex = (clip.ArchitecturePayload as IArchitectureControlNetSourcePlan)
            ?.ControlNetSourceIndex
            ?? throw new SwarmUserErrorException(
                "VideoStages: ControlNet owns clip length, but the compiled plan has no valid "
                + "ControlNet 1-3 source.");
        if (!TryApplyControlNetFrameCount(sourceIndex))
        {
            throw new SwarmUserErrorException(
                $"VideoStages: ControlNet {sourceIndex + 1} owns clip {clip.ClipId} length, "
                + "but its captured video frame count is unavailable.");
        }
    }

    private bool TryApplyControlNetFrameCount(int controlNetSourceIndex)
    {
        if (!new ControlNetCapture(g).TryCreateCapturedControlImageFrameCount(
                controlNetSourceIndex,
                out JArray framesConnection))
        {
            return false;
        }
        LtxFrameCountConnector.ApplyToExistingSources(g, framesConnection);
        return true;
    }
}
