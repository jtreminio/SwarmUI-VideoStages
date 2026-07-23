using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;

namespace VideoStages.LTX2;

internal static class LtxAudioReuseState
{
    private static bool IsValidAudioLatentPath(JArray path)
    {
        return path is { Count: 2 };
    }

    public static void PrepareReusableAudio(
        WorkflowGenerator generator,
        ClipContext clipContext,
        StagePlan stage)
    {
        if (generator.CurrentMedia is null)
        {
            return;
        }

        ClipSpec clip = clipContext.Clip;
        ClipAudioState audioReuse = clipContext.AudioReuse;
        if (stage.AudioAction == StageAudioAction.None)
        {
            audioReuse.Clear();
            return;
        }

        if (stage.AudioAction == StageAudioAction.CaptureForReuse
            && generator.CurrentMedia.AttachedAudio?.DataType == WGNodeData.DT_LATENT_AUDIO
            && generator.CurrentMedia.AttachedAudio.Path is JArray currentAudioPath
            && IsValidAudioLatentPath(currentAudioPath))
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
}
