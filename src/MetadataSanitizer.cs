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
        foreach (JProperty dataProp in upload.Properties().Where(p => StringUtils.Equals(p.Name, "Data")).ToList())
        {
            dataProp.Remove();
        }
        if (!upload.HasValues)
        {
            foreach (JProperty containerProp in parent.Properties().Where(p => StringUtils.Equals(p.Name, containerKey)).ToList())
            {
                containerProp.Remove();
            }
        }
    }

    private static JToken GetProperty(JObject obj, string name)
    {
        foreach (JProperty property in obj.Properties())
        {
            if (StringUtils.Equals(property.Name, name))
            {
                return property.Value;
            }
        }
        return null;
    }
}
