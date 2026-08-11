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
            if (!TryParseKind(rawKind, out ClipReferenceKind kind))
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
            string where = $"Clip {clipIndex} reference {index}";
            references.Add(new ClipReferenceSpec(
                kind,
                source.Trim(),
                media,
                DocumentJson.GetOptionalBool(raw[index], "includeSoundtrack", false),
                ReferenceScale.Normalize(scale),
                Math.Max(
                    0,
                    DocumentJson.GetOptionalDouble(
                        raw[index], "startSeconds", 0, where, warn)),
                Math.Max(
                    0,
                    DocumentJson.GetOptionalDouble(
                        raw[index], "lengthSeconds", 0, where, warn))));
        }
        return references;
    }

    /// <summary>The enum names are the wire tokens.</summary>
    public static string WireName(ClipReferenceKind kind) => kind.ToString().ToLowerInvariant();

    public static bool TryParseKind(string raw, out ClipReferenceKind kind)
    {
        string compact = StringUtils.Compact(raw);
        foreach (ClipReferenceKind candidate in Enum.GetValues<ClipReferenceKind>())
        {
            if (StringUtils.Equals(compact, WireName(candidate)))
            {
                kind = candidate;
                return true;
            }
        }
        kind = default;
        return false;
    }
}
