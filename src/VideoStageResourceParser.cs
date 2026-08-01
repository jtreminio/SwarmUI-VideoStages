using System.Globalization;
using Newtonsoft.Json.Linq;

namespace VideoStages;

internal static class VideoStageResourceParser
{
    internal const int IcLoraStrengthMin = 0;
    internal const int IcLoraStrengthMax = 5;

    public static IReadOnlyList<ImageRefSpec> ParseImageReferences(
        JObject clipObject,
        int clipIndex,
        Action<string> warn = null)
    {
        List<JObject> rawReferences = VideoStagesJsonReader.GetObjectArray(
            clipObject, UploadContainers.RefsCollection);
        List<ImageRefSpec> references = [];
        for (int index = 0; index < rawReferences.Count; index++)
        {
            ImageRefSpec parsed = ParseImageReference(
                rawReferences[index],
                clipIndex,
                index,
                warn);
            if (parsed is not null)
            {
                references.Add(parsed);
            }
        }
        return references;
    }

    public static IReadOnlyList<LoraRef> ParseLoras(
        JObject obj,
        Action<string> warn = null)
    {
        List<LoraRef> loras = [];
        foreach (JObject entry in VideoStagesJsonReader.GetObjectArray(obj, "loras"))
        {
            string name = VideoStagesJsonReader.GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            double weight = SanitizeWeight(
                VideoStagesJsonReader.GetOptionalDouble(
                    entry, "weight", 1.0, "lora", warn),
                1.0);
            double? textEncoderWeight = null;
            if (VideoStagesJsonReader.HasProperty(entry, "textEncoderWeight"))
            {
                textEncoderWeight = SanitizeWeight(
                    VideoStagesJsonReader.GetOptionalDouble(
                        entry, "textEncoderWeight", weight, "lora", warn),
                    weight);
            }
            loras.Add(new LoraRef(name.Trim(), weight, textEncoderWeight));
        }
        return loras;
    }

    public static IReadOnlyList<double> ParseLoraWeights(JObject obj)
    {
        if (!VideoStagesJsonReader.HasProperty(obj, "loraWeights"))
        {
            return null;
        }
        if (VideoStagesJsonReader.GetArray(obj, "loraWeights") is not JArray array)
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

    public static IReadOnlyList<IcLoraSpec> ParseIcLoras(
        JObject clipObject,
        Action<string> warn = null)
    {
        List<IcLoraSpec> entries = [];
        List<JObject> rawEntries = VideoStagesJsonReader.GetObjectArray(
            clipObject, UploadContainers.IcLorasCollection);
        for (int index = 0; index < rawEntries.Count; index++)
        {
            JObject entry = rawEntries[index];
            string lora = NormalizeLoraName(VideoStagesJsonReader.GetString(entry, "lora"));
            if (lora.Length == 0)
            {
                continue;
            }
            UploadedMediaSpec driveMedia = VideoStagesJsonReader.GetEmbeddedUpload(
                entry,
                UploadContainers.IcLoraDriveMedia);
            string driveSource = VideoStagesJsonReader.GetString(entry, "driveSource")?.Trim();
            string rawDriveData = VideoStagesJsonReader.GetString(entry, "driveData");
            entries.Add(new IcLoraSpec(
                Lora: lora,
                Preset: VideoStagesJsonReader.GetString(entry, "preset")?.Trim(),
                Stage: Math.Max(-1, (int)VideoStagesJsonReader.GetOptionalDouble(
                    entry, "stage", -1, "Clip IcLora", warn)),
                DriveSource: driveSource,
                Strength: Math.Clamp(VideoStagesJsonReader.GetOptionalDouble(
                    entry, "strength", 1, "Clip IcLora", warn), IcLoraStrengthMin, IcLoraStrengthMax),
                AttentionStrength: Math.Clamp(VideoStagesJsonReader.GetOptionalDouble(
                    entry, "attentionStrength", 1, "Clip IcLora", warn), 0, 1),
                ControlType: VideoStagesJsonReader.GetString(entry, "controlType")?.Trim(),
                DriveMedia: driveMedia,
                DriveData: ParseDriveData(rawDriveData, index, warn),
                DriveMediaKinds: ParseDriveMediaKinds(entry, index, warn)));
        }
        return entries;
    }

    private static IcLoraDriveData ParseDriveData(
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
        VideoStagesJsonReader.Warn(
            warn,
            $"VideoStages: IC-LoRA {entryIndex} has unsupported DriveData '{rawValue}'; "
                + "using None.");
        return IcLoraDriveData.None;
    }

    private static IReadOnlyList<string> ParseDriveMediaKinds(
        JObject entry,
        int entryIndex,
        Action<string> warn)
    {
        JToken token = VideoStagesJsonReader.GetToken(entry, "driveMediaKinds");
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }
        if (token is not JArray array)
        {
            VideoStagesJsonReader.Warn(
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
                VideoStagesJsonReader.Warn(
                    warn,
                    $"VideoStages: IC-LoRA {entryIndex} DriveMediaKinds item {itemIndex} "
                        + "must be image, video, or audio; ignoring it.");
                continue;
            }
            if (!seen.Add(kind))
            {
                VideoStagesJsonReader.Warn(
                    warn,
                    $"VideoStages: IC-LoRA {entryIndex} DriveMediaKinds repeats '{kind}'; "
                        + "ignoring the duplicate.");
                continue;
            }
            kinds.Add(kind);
        }
        return kinds;
    }

    private static string NormalizeDriveMediaKind(JToken token)
    {
        if (token.Type != JTokenType.String)
        {
            return null;
        }
        string compact = StringUtils.Compact(token.Value<string>());
        if (StringUtils.Equals(compact, "image"))
        {
            return "image";
        }
        if (StringUtils.Equals(compact, "video"))
        {
            return "video";
        }
        if (StringUtils.Equals(compact, "audio"))
        {
            return "audio";
        }
        return null;
    }

    private static ImageRefSpec ParseImageReference(
        JObject obj,
        int clipIndex,
        int refIndex,
        Action<string> warn)
    {
        string source = VideoStagesJsonReader.GetString(obj, "source");
        if (string.IsNullOrWhiteSpace(source))
        {
            VideoStagesJsonReader.Warn(
                warn,
                $"VideoStages: Clip {clipIndex} reference {refIndex} is missing a Source value; skipping.");
            return null;
        }

        int frame = 1;
        string rawFrame = VideoStagesJsonReader.GetString(obj, "frame");
        if (!string.IsNullOrWhiteSpace(rawFrame) && int.TryParse(rawFrame.Trim(), out int parsedFrame))
        {
            frame = Math.Max(1, parsedFrame);
        }

        bool fromEnd = false;
        string rawFromEnd = VideoStagesJsonReader.GetString(obj, "fromEnd");
        if (!string.IsNullOrWhiteSpace(rawFromEnd)
            && bool.TryParse(rawFromEnd.Trim(), out bool parsedFromEnd))
        {
            fromEnd = parsedFromEnd;
        }

        string uploadFileName = VideoStagesJsonReader.GetString(obj, "uploadFileName");
        string data = VideoStagesJsonReader.GetString(obj, "data");
        UploadedMediaSpec embeddedImage = VideoStagesJsonReader.GetEmbeddedUpload(
            obj, UploadContainers.RefImage);
        if (embeddedImage is not null)
        {
            data = embeddedImage.Data;
            if (string.IsNullOrWhiteSpace(uploadFileName)
                && !string.IsNullOrWhiteSpace(embeddedImage.FileName))
            {
                uploadFileName = embeddedImage.FileName;
            }
        }

        return new ImageRefSpec(
            source.Trim(),
            frame,
            fromEnd,
            string.IsNullOrWhiteSpace(uploadFileName) ? null : uploadFileName.Trim(),
            string.IsNullOrWhiteSpace(data) ? null : data.Trim());
    }

    private static double SanitizeWeight(double value, double fallback) =>
        IsFinite(value) ? value : fallback;

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

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

}
