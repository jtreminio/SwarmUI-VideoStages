using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.LTX2;

internal static class LtxAudioReuseState
{
    private const string ReusedAudioLatentNodeHelperKey = "videostages.reused-audio-latent";

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
            && currentAudioPath.Count == 2)
        {
            Remember(generator, currentAudioPath);
        }

        if (!TryGetPath(generator, out JArray reusedAudioPath))
        {
            return;
        }

        WGNodeData currentMedia = generator.CurrentMedia.Duplicate();
        currentMedia.AttachedAudio = new WGNodeData(
            reusedAudioPath,
            generator,
            WGNodeData.DT_LATENT_AUDIO,
            generator.CurrentAudioVae?.Compat ?? generator.CurrentMedia.AttachedAudio?.Compat ?? T2IModelClassSorter.CompatLtxv2);
        generator.CurrentMedia = currentMedia;
    }

    public static void Remember(WorkflowGenerator generator, JArray audioLatentPath)
    {
        if (generator is null
            || audioLatentPath is null
            || audioLatentPath.Count != 2)
        {
            return;
        }

        generator.NodeHelpers[ReusedAudioLatentNodeHelperKey] = audioLatentPath.ToString(Formatting.None);
    }

    public static void Clear(WorkflowGenerator generator)
    {
        _ = generator?.NodeHelpers.Remove(ReusedAudioLatentNodeHelperKey);
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
            if (JToken.Parse(encodedPath) is not JArray parsedPath || parsedPath.Count != 2)
            {
                return false;
            }

            reusedAudioLatentPath = new JArray(parsedPath[0], parsedPath[1]);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
