using Newtonsoft.Json.Linq;

namespace VideoStages.Authoring;

internal static class ClipReferences
{
    public static IReadOnlyList<ClipReferenceSpec> Read(
        JObject clipObject,
        int clipIndex,
        Action<string> warn = null)
    {
        List<JObject> raw = DocumentJson.GetObjectArray(
            clipObject, UploadContainers.ClipReferencesCollection);
        List<ClipReferenceSpec> references = [];
        for (int index = 0; index < raw.Count; index++)
        {
            string rawKind = DocumentJson.GetString(raw[index], "kind")?.Trim();
            ClipReferenceKind? kind = rawKind?.ToLowerInvariant() switch
            {
                "image" => ClipReferenceKind.Image,
                "video" => ClipReferenceKind.Video,
                "audio" => ClipReferenceKind.Audio,
                _ => null,
            };
            if (kind is null)
            {
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: Clip {clipIndex} reference {index} has unknown kind "
                        + $"'{rawKind}'; skipping.");
                continue;
            }
            string source = DocumentJson.GetString(raw[index], "source");
            if (string.IsNullOrWhiteSpace(source))
            {
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: Clip {clipIndex} reference {index} is missing a Source "
                        + "value; skipping.");
                continue;
            }
            UploadedMediaSpec media = DocumentJson.GetEmbeddedUpload(
                raw[index], UploadContainers.ClipReferenceMedia);
            double scale = DocumentJson.GetOptionalDouble(
                raw[index],
                "mediaScale",
                ReferenceScale.Full,
                $"Clip {clipIndex} reference {index}",
                warn);
            references.Add(new ClipReferenceSpec(
                kind.Value,
                source.Trim(),
                media,
                DocumentJson.GetOptionalBool(raw[index], "includeSoundtrack", false),
                ReferenceScale.Normalize(scale)));
        }
        return references;
    }
}
