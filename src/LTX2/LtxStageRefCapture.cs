using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal static class LtxStageRefCapture
{
    internal static void ApplyPostVideoChainCaptureIfPresent(
        WorkflowGenerator g,
        ref WGNodeData referenceMedia,
        ref WGNodeData referenceVae)
    {
        LtxPostVideoChain postVideoChain = LtxPostVideoChain.TryCapture(g);
        if (postVideoChain is null)
        {
            return;
        }

        referenceMedia = postVideoChain.CreateStageInput();
        referenceVae = postVideoChain.CreateStageInputVae();
    }
}
