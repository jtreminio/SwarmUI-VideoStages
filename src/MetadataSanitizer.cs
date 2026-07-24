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
            if (JToken.Parse(raw) is not JObject root)
            {
                return raw;
            }
            foreach (UploadContainerPath path in UploadContainers.AllPaths)
            {
                Walk(root, path, stepIndex: 0);
            }
            return root.ToString(Formatting.None);
        }
        catch
        {
            return raw;
        }
    }

    private static void Walk(JObject parent, UploadContainerPath path, int stepIndex)
    {
        if (stepIndex >= path.Steps.Count)
        {
            StripUploadContainer(parent, path.Container);
            return;
        }
        UploadPathStep step = path.Steps[stepIndex];
        JToken next = JsonUtil.Get(parent, step.Name);
        if (!step.IsArray)
        {
            if (next is JObject child)
            {
                Walk(child, path, stepIndex + 1);
            }
            return;
        }
        if (next is not JArray items)
        {
            return;
        }
        foreach (JToken item in items)
        {
            if (item is JObject element)
            {
                Walk(element, path, stepIndex + 1);
            }
        }
    }

    private static void StripUploadContainer(JObject parent, string containerKey)
    {
        if (JsonUtil.Get(parent, containerKey) is not JObject upload)
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
