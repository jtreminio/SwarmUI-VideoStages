using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxRetakeMaskApplicator(WorkflowGenerator g)
{
    internal WGNodeData ApplyIfActive(
        WGNodeData encodedLatent,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        RetakePlan retake,
        bool active)
    {
        if (!active || retake is null)
        {
            return encodedLatent;
        }

        return new LtxVideoRetakeMasker(g).Apply(
            encodedLatent,
            genInfo,
            retake);
    }
}
