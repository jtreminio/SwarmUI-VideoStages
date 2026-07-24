using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>
/// Parses model-adjacent persisted values without interpreting architecture-specific semantics.
/// </summary>
internal static class VideoStageResourceParser
{
    public static IReadOnlyList<ImageRefSpec> ParseImageReferences(JObject clipObject, int clipIndex)
    {
        List<JObject> rawReferences = VideoStagesJsonReader.GetObjectArray(
            clipObject, UploadContainers.RefsCollection);
        List<ImageRefSpec> references = [];
        for (int index = 0; index < rawReferences.Count; index++)
        {
            ImageRefSpec parsed = ParseImageReference(rawReferences[index], clipIndex, index);
            if (parsed is not null)
            {
                references.Add(parsed);
            }
        }
        return references;
    }

    public static IReadOnlyList<LoraRef> ParseLoras(JObject obj)
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
                VideoStagesJsonReader.GetOptionalDouble(entry, "weight", 1.0, "lora"), 1.0);
            double? textEncoderWeight = null;
            if (VideoStagesJsonReader.HasProperty(entry, "textEncoderWeight"))
            {
                textEncoderWeight = SanitizeWeight(
                    VideoStagesJsonReader.GetOptionalDouble(
                        entry, "textEncoderWeight", weight, "lora"),
                    weight);
            }
            loras.Add(new LoraRef(name.Trim(), weight, textEncoderWeight));
        }
        return loras;
    }

    public static IReadOnlyList<IcLoraSpec> ParseIcLoras(JObject clipObject)
    {
        List<IcLoraSpec> entries = [];
        foreach (JObject entry in VideoStagesJsonReader.GetObjectArray(
            clipObject, UploadContainers.IcLorasCollection))
        {
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
                    entry, "stage", -1, "Clip IcLora")),
                DriveSource: driveSource,
                Strength: Math.Clamp(VideoStagesJsonReader.GetOptionalDouble(
                    entry, "strength", 1, "Clip IcLora"), 0, 5),
                AttentionStrength: Math.Clamp(VideoStagesJsonReader.GetOptionalDouble(
                    entry, "attentionStrength", 1, "Clip IcLora"), 0, 1),
                ControlType: VideoStagesJsonReader.GetString(entry, "controlType")?.Trim(),
                DriveMedia: driveMedia,
                DriveData: ParseDriveData(rawDriveData),
                DriveMediaKinds: ParseDriveMediaKinds(entry)));
        }
        return entries;
    }

    private static IcLoraDriveData ParseDriveData(string rawValue)
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
        return (IcLoraDriveData)(-1);
    }

    private static IReadOnlyList<string> ParseDriveMediaKinds(JObject entry)
    {
        JToken token = VideoStagesJsonReader.GetToken(entry, "driveMediaKinds");
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }
        if (token is not JArray array)
        {
            return [$"[invalid-list:{token.Type}]"];
        }
        return
        [
            .. array.Select(item => item.Type == JTokenType.String
                ? item.Value<string>()
                : $"[invalid-kind:{item.Type}]"),
        ];
    }

    private static ImageRefSpec ParseImageReference(JObject obj, int clipIndex, int refIndex)
    {
        string source = VideoStagesJsonReader.GetString(obj, "source");
        if (string.IsNullOrWhiteSpace(source))
        {
            Logs.Warning(
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
