using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Resolves contextual Incoming IC-LoRA media without changing the stage's sampler input.</summary>
internal sealed class IcLoraStageInputResolver(WorkflowGenerator g)
{
    public WGNodeData Resolve(StageFrame stageFrame)
    {
        ArgumentNullException.ThrowIfNull(stageFrame);
        bool wantsIncoming = stageFrame.Stage.RequireLtx2Payload().IcLoras.Any(entry =>
            entry.MediaInput.Source == IcLoraMediaSourceKind.Incoming);
        if (!wantsIncoming)
        {
            return null;
        }
        WGNodeData source = stageFrame.ClipContext.IsFirstStage(stageFrame.Stage)
            ? stageFrame.ClipContext.IcLoraEntryIncomingMedia
            : stageFrame.SourceMedia;
        LtxPostVideoChainCapture postVideoChain = stageFrame.PostVideoChain;
        if (postVideoChain is null
            || !postVideoChain.ReferencesOutput(source))
        {
            return source;
        }
        WGNodeData detached = postVideoChain.CreateDetachedGuideMedia(g.CurrentVae);
        if (detached is null)
        {
            return source;
        }
        detached.AttachedAudio = source?.AttachedAudio;
        return detached;
    }
}
