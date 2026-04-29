using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.LTX2;

internal static class LtxAudioReuseState
{
    private const string ReusedAudioLatentNodeHelperKey = "videostages.reused-audio-latent";

    private static bool IsValidAudioLatentPath(JArray? path)
    {
        return path is not null && path.Count == 2;
    }

    public static void PrepareReusableAudio(WorkflowGenerator generator, JsonParser.StageSpec stage)
    {
        if (generator?.CurrentMedia is null || stage is null)
        {
            return;
        }

        bool clipCanReuseAudio = stage.ClipReuseAudio && stage.ClipStageCount >= 3;
        if (!clipCanReuseAudio || stage.ClipStageIndex == 0)
        {
            Clear(generator);
            return;
        }

        if (stage.ClipStageIndex == 1
            && generator.CurrentMedia.AttachedAudio?.DataType == WGNodeData.DT_LATENT_AUDIO
            && generator.CurrentMedia.AttachedAudio.Path is JArray currentAudioPath
            && IsValidAudioLatentPath(currentAudioPath))
        {
            Remember(generator, currentAudioPath);
        }

        if (!TryGetPath(generator, out JArray reusedAudioPath))
        {
            return;
        }

        T2IModelCompatClass audioCompat = generator.CurrentAudioVae?.Compat
            ?? generator.CurrentMedia.AttachedAudio?.Compat
            ?? T2IModelClassSorter.CompatLtxv2;
        WGNodeData currentMedia = generator.CurrentMedia.Duplicate();
        currentMedia.AttachedAudio = new WGNodeData(
            reusedAudioPath,
            generator,
            WGNodeData.DT_LATENT_AUDIO,
            audioCompat);
        generator.CurrentMedia = currentMedia;
    }

    public static void Remember(WorkflowGenerator generator, JArray audioLatentPath)
    {
        if (generator is null || !IsValidAudioLatentPath(audioLatentPath))
        {
            return;
        }

        generator.NodeHelpers[ReusedAudioLatentNodeHelperKey] = audioLatentPath.ToString(Formatting.None);
    }

    public static void Clear(WorkflowGenerator generator)
    {
        generator.NodeHelpers.Remove(ReusedAudioLatentNodeHelperKey);
    }

    public static bool TryGetPath(WorkflowGenerator generator, out JArray reusedAudioLatentPath)
    {
        reusedAudioLatentPath = null;
        if (generator is null
            || !generator.NodeHelpers.TryGetValue(ReusedAudioLatentNodeHelperKey, out string encodedPath)
            || string.IsNullOrWhiteSpace(encodedPath))
        {
            return false;
        }

        try
        {
            if (JToken.Parse(encodedPath) is not JArray parsedPath || !IsValidAudioLatentPath(parsedPath))
            {
                return false;
            }

            reusedAudioLatentPath = new JArray(parsedPath[0], parsedPath[1]);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
