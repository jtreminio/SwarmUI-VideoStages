using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Detaches stage-input IC-LoRA guide media from a mutable post-video chain.</summary>
internal sealed class IcLoraStageInputResolver(WorkflowGenerator g)
{
    public WGNodeData Resolve(StageFrame stageFrame)
    {
        ArgumentNullException.ThrowIfNull(stageFrame);
        WGNodeData source = stageFrame.SourceMedia;
        LtxPostVideoChainCapture postVideoChain = stageFrame.PostVideoChain;
        bool wantsStageInput = stageFrame.Stage.RequireLtx2Payload().IcLoras.Any(entry =>
            entry.Drive.Kind
                is IcLoraDriveSourceKind.StageInput
                or IcLoraDriveSourceKind.SourcedClipInput);
        if (!wantsStageInput
            || postVideoChain is null
            || !StagePostVideoChainMedia.ReferencesOutput(source, postVideoChain))
        {
            return source;
        }
        return postVideoChain.CreateDetachedGuideMedia(g.CurrentVae) ?? source;
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
