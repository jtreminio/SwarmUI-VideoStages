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
            JArray clips = root is JObject obj ? GetProperty(obj, "clips") as JArray : null;
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
        foreach ((string collection, string container) in UploadContainers.All)
        {
            if (collection is null)
            {
                StripUploadContainer(clip, container);
                continue;
            }
            if (GetProperty(clip, collection) is not JArray items)
            {
                continue;
            }
            foreach (JToken itemToken in items)
            {
                if (itemToken is JObject itemObj)
                {
                    StripUploadContainer(itemObj, container);
                }
            }
        }
    }

    private static void StripUploadContainer(JObject parent, string containerKey)
    {
        if (GetProperty(parent, containerKey) is not JObject upload)
        {
            return;
        }
        upload.Remove("data");
        if (!upload.HasValues)
        {
            parent.Remove(containerKey);
        }
    }

    private static JToken GetProperty(JObject obj, string name) => JsonUtil.Get(obj, name);
}
