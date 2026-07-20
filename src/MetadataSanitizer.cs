using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VideoStages;

internal static class MetadataSanitizer
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
            JArray clips = root switch
            {
                JArray array => array,
                JObject obj => GetProperty(obj, "Clips") as JArray,
                _ => null,
            };
            if (clips is null)
            {
                return raw;
            }
            foreach (JToken clipToken in clips)
            {
                if (clipToken is JObject clip)
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
        StripUploadContainer(clip, "UploadedAudio");
        if (GetProperty(clip, "AudioSegments") is JArray audioSegments)
        {
            foreach (JToken segToken in audioSegments)
            {
                if (segToken is JObject segObj)
                {
                    StripUploadContainer(segObj, "Source");
                }
            }
        }
        if (GetProperty(clip, "IcLoras") is JArray icLoras)
        {
            foreach (JToken entryToken in icLoras)
            {
                if (entryToken is JObject entryObj)
                {
                    StripUploadContainer(entryObj, "Video");
                }
            }
        }
        if (GetProperty(clip, "Refs") is not JArray refs)
        {
            return;
        }
        foreach (JToken refToken in refs)
        {
            if (refToken is JObject refObj)
            {
                StripUploadContainer(refObj, "UploadedImage");
            }
        }
    }

    private static void StripUploadContainer(JObject parent, string containerKey)
    {
        if (GetProperty(parent, containerKey) is not JObject upload)
        {
            return;
        }
        JsonUtil.RemoveAll(upload, "Data");
        if (!upload.HasValues)
        {
            JsonUtil.RemoveAll(parent, containerKey);
        }
    }

    private static JToken GetProperty(JObject obj, string name) => JsonUtil.Get(obj, name);
}
