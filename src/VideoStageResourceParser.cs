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
        foreach (JObject entry in VideoStagesJsonReader.GetObjectArray(obj, "Loras"))
        {
            string name = VideoStagesJsonReader.GetString(entry, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            double weight = SanitizeWeight(
                VideoStagesJsonReader.GetOptionalDouble(entry, "Weight", 1.0, "Lora"), 1.0);
            double? textEncoderWeight = null;
            if (VideoStagesJsonReader.HasProperty(entry, "TextEncoderWeight"))
            {
                textEncoderWeight = SanitizeWeight(
                    VideoStagesJsonReader.GetOptionalDouble(
                        entry, "TextEncoderWeight", weight, "Lora"),
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
            string lora = NormalizeLoraName(VideoStagesJsonReader.GetString(entry, "Lora"));
            if (lora.Length == 0)
            {
                continue;
            }

            entries.Add(new IcLoraSpec(
                Lora: lora,
                Preset: VideoStagesJsonReader.GetString(entry, "Preset")?.Trim(),
                Stage: Math.Max(-1, (int)VideoStagesJsonReader.GetOptionalDouble(
                    entry, "Stage", -1, "Clip IcLora")),
                Source: VideoStagesJsonReader.GetString(entry, "Source")?.Trim(),
                Strength: Math.Clamp(VideoStagesJsonReader.GetOptionalDouble(
                    entry, "Strength", 1, "Clip IcLora"), 0, 5),
                AttentionStrength: Math.Clamp(VideoStagesJsonReader.GetOptionalDouble(
                    entry, "AttentionStrength", 1, "Clip IcLora"), 0, 1),
                ControlType: VideoStagesJsonReader.GetString(entry, "ControlType")?.Trim(),
                DriveMedia: VideoStagesJsonReader.GetEmbeddedUpload(
                    entry,
                    UploadContainers.IcLoraDriveMedia)));
        }
        return entries;
    }

    private static ImageRefSpec ParseImageReference(JObject obj, int clipIndex, int refIndex)
    {
        string source = VideoStagesJsonReader.GetString(obj, "Source");
        if (string.IsNullOrWhiteSpace(source))
        {
            Logs.Warning(
                $"VideoStages: Clip {clipIndex} reference {refIndex} is missing a Source value; skipping.");
            return null;
        }

        int frame = 1;
        string rawFrame = VideoStagesJsonReader.GetString(obj, "Frame");
        if (!string.IsNullOrWhiteSpace(rawFrame) && int.TryParse(rawFrame.Trim(), out int parsedFrame))
        {
            frame = Math.Max(1, parsedFrame);
        }

        bool fromEnd = false;
        string rawFromEnd = VideoStagesJsonReader.GetString(obj, "FromEnd");
        if (!string.IsNullOrWhiteSpace(rawFromEnd)
            && bool.TryParse(rawFromEnd.Trim(), out bool parsedFromEnd))
        {
            fromEnd = parsedFromEnd;
        }

        string uploadFileName = VideoStagesJsonReader.GetString(obj, "UploadFileName");
        string data = VideoStagesJsonReader.GetString(obj, "Data");
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
