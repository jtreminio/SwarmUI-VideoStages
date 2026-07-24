using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Captures the current decoded stage reference, peeling the LTX post-video chain when present.
/// </summary>
internal sealed class LtxStageReferenceCapture(WorkflowGenerator g)
{
    internal StageRefStore.StageRef Capture()
    {
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        LtxPostVideoChainCapture postVideoChain = LtxPostVideoChainCapture.TryCapture(g);
        if (postVideoChain is not null)
        {
            referenceMedia = postVideoChain.CreateStageInput();
            referenceVae = postVideoChain.CreateStageInputVae();
        }
        return new(referenceMedia, referenceVae);
    }
}
