using System.Globalization;
using Newtonsoft.Json.Linq;

namespace VideoStages.Authoring;

internal static class Loras
{
    internal const int IcLoraStrengthMin = 0;
    internal const int IcLoraStrengthMax = 5;

    public static IReadOnlyList<LoraRef> ReadNormal(
        JObject obj,
        Action<string> warn = null)
    {
        List<LoraRef> loras = [];
        foreach (JObject entry in DocumentJson.GetObjectArray(obj, "loras"))
        {
            string name = DocumentJson.GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            double weight = SanitizeWeight(
                DocumentJson.GetOptionalDouble(
                    entry, "weight", 1.0, "lora", warn),
                1.0);
            double? textEncoderWeight = null;
            if (DocumentJson.HasProperty(entry, "textEncoderWeight"))
            {
                textEncoderWeight = SanitizeWeight(
                    DocumentJson.GetOptionalDouble(
                        entry, "textEncoderWeight", weight, "lora", warn),
                    weight);
            }
            loras.Add(new LoraRef(name.Trim(), weight, textEncoderWeight));
        }
        return loras;
    }

    public static IReadOnlyList<double> ReadWeights(JObject obj)
    {
        if (!DocumentJson.HasProperty(obj, "loraWeights"))
        {
            return null;
        }
        if (DocumentJson.GetArray(obj, "loraWeights") is not JArray array)
        {
            return [];
        }
        List<double> weights = [];
        foreach (JToken entry in array)
        {
            double value;
            if (entry.Type is JTokenType.Float or JTokenType.Integer)
            {
                value = entry.Value<double>();
            }
            else if (
                entry.Type == JTokenType.String
                && double.TryParse(
                    $"{entry}".Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsed))
            {
                value = parsed;
            }
            else
            {
                value = 1;
            }
            weights.Add(SanitizeWeight(value, 1));
        }
        return weights.AsReadOnly();
    }

    public static IReadOnlyList<IcLoraSpec> ReadIc(
        JObject clipObject,
        Action<string> warn = null)
    {
        List<IcLoraSpec> entries = [];
        List<JObject> rawEntries = DocumentJson.GetObjectArray(
            clipObject, UploadContainers.IcLorasCollection);
        for (int index = 0; index < rawEntries.Count; index++)
        {
            JObject entry = rawEntries[index];
            string lora = NormalizeLoraName(DocumentJson.GetString(entry, "lora"));
            if (lora.Length == 0)
            {
                continue;
            }
            UploadedMediaSpec driveMedia = DocumentJson.GetEmbeddedUpload(
                entry,
                UploadContainers.IcLoraDriveMedia);
            string driveSource = DocumentJson.GetString(entry, "driveSource")?.Trim();
            string rawDriveData = DocumentJson.GetString(entry, "driveData");
            entries.Add(new IcLoraSpec(
                Lora: lora,
                Preset: DocumentJson.GetString(entry, "preset")?.Trim(),
                Stage: Math.Max(-1, (int)DocumentJson.GetOptionalDouble(
                    entry, "stage", -1, "Clip IcLora", warn)),
                DriveSource: driveSource,
                Strength: Math.Clamp(DocumentJson.GetOptionalDouble(
                    entry, "strength", 1, "Clip IcLora", warn), IcLoraStrengthMin, IcLoraStrengthMax),
                AttentionStrength: Math.Clamp(DocumentJson.GetOptionalDouble(
                    entry, "attentionStrength", 1, "Clip IcLora", warn), 0, 1),
                ControlType: DocumentJson.GetString(entry, "controlType")?.Trim(),
                DriveMedia: driveMedia,
                DriveData: ReadDriveData(rawDriveData, index, warn),
                DriveMediaKinds: ReadDriveMediaKinds(entry, index, warn)));
        }
        return entries;
    }

    private static IcLoraDriveData ReadDriveData(
        string rawValue,
        int entryIndex,
        Action<string> warn)
    {
        string raw = StringUtils.Compact(rawValue);
        if (raw.Length == 0)
        {
            return IcLoraDriveData.None;
        }
        if (StringUtils.Equals(raw, nameof(IcLoraDriveData.Visual)))
        {
            return IcLoraDriveData.Visual;
        }
        if (StringUtils.Equals(raw, nameof(IcLoraDriveData.Audio)))
        {
            return IcLoraDriveData.Audio;
        }
        if (StringUtils.Equals(raw, nameof(IcLoraDriveData.None)))
        {
            return IcLoraDriveData.None;
        }
        DocumentJson.Warn(
            warn,
            $"VideoStages: IC-LoRA {entryIndex} has unsupported DriveData '{rawValue}'; "
                + "using None.");
        return IcLoraDriveData.None;
    }

    private static IReadOnlyList<string> ReadDriveMediaKinds(
        JObject entry,
        int entryIndex,
        Action<string> warn)
    {
        JToken token = DocumentJson.GetToken(entry, "driveMediaKinds");
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }
        if (token is not JArray array)
        {
            DocumentJson.Warn(
                warn,
                $"VideoStages: IC-LoRA {entryIndex} DriveMediaKinds must be an array; ignoring it.");
            return null;
        }

        List<string> kinds = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int itemIndex = 0; itemIndex < array.Count; itemIndex++)
        {
            string kind = NormalizeDriveMediaKind(array[itemIndex]);
            if (kind is null)
            {
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: IC-LoRA {entryIndex} DriveMediaKinds item {itemIndex} "
                        + "must be image, video, or audio; ignoring it.");
                continue;
            }
            if (!seen.Add(kind))
            {
                DocumentJson.Warn(
                    warn,
                    $"VideoStages: IC-LoRA {entryIndex} DriveMediaKinds repeats '{kind}'; "
                        + "ignoring the duplicate.");
                continue;
            }
            kinds.Add(kind);
        }
        return kinds;
    }

    private static string NormalizeDriveMediaKind(JToken token) =>
        token.Type == JTokenType.String
            && ClipReferences.TryParseKind(token.Value<string>(), out ClipReferenceKind kind)
            ? ClipReferences.WireName(kind)
            : null;

    private static double SanitizeWeight(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;

    private static string NormalizeLoraName(string raw)
    {
        string trimmed = string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }

        string squeezed = new([.. trimmed.Where(character => !char.IsWhiteSpace(character))]);
        return StringUtils.Equals(squeezed, "(none)") ? "" : trimmed;
    }
}
