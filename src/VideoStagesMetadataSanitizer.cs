using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VideoStages;

internal static class VideoStagesMetadataSanitizer
{
    public static string StripUploadDataFromJsonParameter(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }
        try
        {
            JToken root = JToken.Parse(raw);
            if (root is not JObject rootObj)
            {
                return raw;
            }
            if (rootObj["clips"] is not JArray clips)
            {
                return raw;
            }
            for (int i = 0; i < clips.Count; i++)
            {
                if (clips[i] is JObject clip)
                {
                    ProcessClip(clip);
                }
            }
            return root.ToString(Formatting.None);
        }
        catch
        {
            return raw;
        }
    }

    private static void ProcessClip(JObject clip)
    {
        StripUploadContainer(clip, "uploadedAudio");
        if (clip["refs"] is not JArray refs)
        {
            return;
        }
        for (int i = 0; i < refs.Count; i++)
        {
            if (refs[i] is JObject refObj)
            {
                StripUploadContainer(refObj, "uploadedImage");
            }
        }
    }

    private static void StripUploadContainer(JObject parent, string containerKey)
    {
        if (parent[containerKey] is not JObject upload)
        {
            return;
        }
        upload.Remove("data");
        if (!upload.HasValues)
        {
            parent.Remove(containerKey);
        }
    }
}
