using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace VideoStages;

internal static class MetadataSanitizer
{
    /// <summary>
    /// Published instead of a document the sanitizer could not walk. Returning the original would
    /// publish exactly the base64 uploads this class exists to remove.
    /// </summary>
    internal const string Unsanitizable =
        "{\"error\":\"VideoStages document could not be sanitized; omitted from metadata\"}";

    public static string StripUploadDataFromJsonParameter(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }
        JObject root;
        try
        {
            root = JToken.Parse(raw) as JObject;
        }
        catch (Exception ex)
        {
            return Refuse($"it is not valid JSON ({ex.Message})");
        }
        if (root is null)
        {
            return Refuse("its root is not a Video Stages document object");
        }
        try
        {
            foreach (UploadContainerPath path in UploadContainers.AllPaths)
            {
                Walk(root, path, stepIndex: 0);
            }
            return root.ToString(Formatting.None);
        }
        catch (Exception ex)
        {
            return Refuse($"walking its upload containers failed ({ex.Message})");
        }
    }

    private static string Refuse(string reason)
    {
        Logs.Warning(
            $"VideoStages: the Video Stages document was left out of output metadata because {reason}.");
        return Unsanitizable;
    }

    private static void Walk(JObject parent, UploadContainerPath path, int stepIndex)
    {
        if (stepIndex >= path.Steps.Count)
        {
            StripUploadContainer(parent, path.Container);
            return;
        }
        UploadPathStep step = path.Steps[stepIndex];
        JToken next = DocumentJson.GetToken(parent, step.Name);
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
        if (DocumentJson.GetToken(parent, containerKey) is not JObject upload)
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
