using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using VideoStages.Authoring;

namespace VideoStages;

internal readonly record struct UploadPathStep(string Name, bool IsArray)
{
    internal static UploadPathStep Each(string name) => new(name, IsArray: true);

    internal static UploadPathStep Into(string name) => new(name, IsArray: false);
}

internal sealed record UploadContainerPath(
    IReadOnlyList<UploadPathStep> Steps,
    string Container);

internal static class MetadataSanitizer
{
    /// Must cover every <see cref="DocumentJson.GetEmbeddedUpload"/> call site.
    private static readonly IReadOnlyList<UploadContainerPath> AllPaths =
    [
        new([UploadPathStep.Each(UploadContainers.ClipsCollection)], UploadContainers.ClipAudio),
        new([UploadPathStep.Each(UploadContainers.ClipsCollection)], UploadContainers.ClipInitVideo),
        new(
            [
                UploadPathStep.Each(UploadContainers.ClipsCollection),
                UploadPathStep.Each(UploadContainers.IcLorasCollection)
            ],
            UploadContainers.IcLoraDriveMedia),
        new(
            [
                UploadPathStep.Each(UploadContainers.ClipsCollection),
                UploadPathStep.Each(UploadContainers.FrameRefsCollection)
            ],
            UploadContainers.RefImage),
        new(
            [
                UploadPathStep.Each(UploadContainers.ClipsCollection),
                UploadPathStep.Each(UploadContainers.ClipReferencesCollection)
            ],
            UploadContainers.ClipReferenceMedia),
        new(
            [
                UploadPathStep.Each(UploadContainers.AudioTracksCollection),
                UploadPathStep.Into(UploadContainers.AudioTrackSource)
            ],
            UploadContainers.ClipAudio),
    ];

    /// Never fall back to the original document because it may contain base64 uploads.
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
            foreach (UploadContainerPath path in AllPaths)
            {
                Walk(root, path, stepIndex: 0);
            }
            return string.Join(
                " ",
                root.ToString(Formatting.Indented)
                    .Split('\n')
                    .Select(line => line.Trim()));
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
