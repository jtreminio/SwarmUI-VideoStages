using Newtonsoft.Json.Linq;
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
            || !StagePostVideoChainMedia.ReferencesOutput(source, postVideoChain))
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

internal static class StagePostVideoChainMedia
{
    public static bool ReferencesOutput(
        WGNodeData media,
        LtxPostVideoChainCapture postVideoChain)
    {
        return media?.Path is JArray mediaPath
            && (JToken.DeepEquals(mediaPath, postVideoChain.CurrentOutputMedia?.Path)
                || JToken.DeepEquals(mediaPath, postVideoChain.DecodeOutputPath));
    }
}
