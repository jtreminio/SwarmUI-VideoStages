using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal static class LtxAudioReuseState
{
    public static void PrepareReusableAudio(
        WorkflowGenerator generator,
        ClipContext clipContext,
        StagePlan stage)
    {
        if (generator.CurrentMedia is null)
        {
            return;
        }

        Ltx2ClipAudioReuseState audioReuse = clipContext.AudioReuse;
        StageAudioAction action = stage.RequireLtx2Payload().AudioAction;
        if (action == StageAudioAction.None)
        {
            audioReuse.Clear();
            return;
        }

        if (action == StageAudioAction.CaptureForReuse
            && generator.CurrentMedia.AttachedAudio?.DataType == WGNodeData.DT_LATENT_AUDIO
            && generator.CurrentMedia.AttachedAudio.Path is JArray { Count: 2 } currentAudioPath)
        {
            audioReuse.Remember(new JArray(currentAudioPath[0], currentAudioPath[1]));
            return;
        }

        if (!audioReuse.TryGetPath(out JArray reusedAudioPath))
        {
            return;
        }

        T2IModelCompatClass audioCompat = generator.CurrentAudioVae?.Compat
            ?? generator.CurrentMedia.AttachedAudio?.Compat
            ?? T2IModelClassSorter.CompatLtxv2;
        WGNodeData currentMedia = generator.CurrentMedia.Duplicate();
        currentMedia.AttachedAudio = new WGNodeData(
            new JArray(reusedAudioPath[0], reusedAudioPath[1]),
            generator,
            WGNodeData.DT_LATENT_AUDIO,
            audioCompat);
        generator.CurrentMedia = currentMedia;
    }

    internal static bool UsesCapturedAudio(StagePlan stage) =>
        stage?.RequireLtx2Payload().AudioAction == StageAudioAction.ReuseCaptured;

    internal static void CompletePostVideoChainCapture(
        Ltx2ClipAudioReuseState audioReuse,
        StagePlan stage,
        JArray audioLatentPath)
    {
        if (audioReuse is null
            || audioLatentPath is null
            || stage?.RequireLtx2Payload().AudioAction != StageAudioAction.CaptureForReuse)
        {
            return;
        }
        audioReuse.Remember(audioLatentPath.DeepClone() as JArray);
    }
}

internal sealed class Ltx2ClipAudioReuseState
{
    public JArray ReusedAudioPath { get; private set; }

    public bool TryGetPath(out JArray path)
    {
        path = ReusedAudioPath;
        return path is not null;
    }

    public void Remember(JArray path) => ReusedAudioPath = path;

    public void Clear() => ReusedAudioPath = null;
}
