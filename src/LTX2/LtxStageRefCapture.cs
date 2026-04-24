using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

/// <summary>Maps native LTX decode output to reusable AV-latent stage references when applicable.</summary>
internal static class LtxStageRefCapture
{
    internal static void ApplyPostVideoChainCaptureIfPresent(WorkflowGenerator g, ref WGNodeData referenceMedia, ref WGNodeData referenceVae)
    {
        PostVideoChain postVideoChain = PostVideoChain.TryCapture(g);
        if (postVideoChain is null)
        {
            return;
        }

        referenceMedia = postVideoChain.CreateStageInput();
        referenceVae = postVideoChain.CreateStageInputVae();
    }
}
