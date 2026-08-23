using Newtonsoft.Json.Linq;

namespace VideoStages.Authoring;

public sealed record MiniMaxCharacterRefineSpec(
    IReadOnlyList<string> PictureReferenceIds,
    bool VerifyIdentity,
    string Finder,
    string Subject,
    string Part,
    double SubjectConfidence,
    double PartConfidence,
    int CropWidth,
    int CropHeight)
{
    public const string DefaultFinder = "sam3";
    public const string DefaultSubject = "person";
    public const string DefaultPart = "face";
    public const double DefaultSubjectConfidence = 0.25;
    public const double DefaultPartConfidence = 0.1;
    public const double MinDetectionThreshold = 0;
    public const double MaxDetectionThreshold = 1;
    public const double DetectionThresholdStep = 0.05;
    public const int DefaultCropWidth = 512;
    public const int DefaultCropHeight = 512;
    public const int MinCropDimension = 64;
    public const int MaxCropDimension = 4096;
    public const int CropDimensionStep = 32;

    internal static MiniMaxCharacterRefineSpec Read(JObject stage)
    {
        JObject raw = DocumentJson.GetObject(stage, "characterRefine");
        if (raw is null)
        {
            return null;
        }
        double subjectConfidence = DocumentJson.GetOptionalDouble(
            raw,
            "subjectConfidence",
            DefaultSubjectConfidence,
            "Make Them Bonita");
        if (!double.IsFinite(subjectConfidence))
        {
            subjectConfidence = DefaultSubjectConfidence;
        }
        double partConfidence = DocumentJson.GetOptionalDouble(
            raw,
            "partConfidence",
            DefaultPartConfidence,
            "Make Them Bonita");
        if (!double.IsFinite(partConfidence))
        {
            partConfidence = DefaultPartConfidence;
        }
        IReadOnlyList<string> pictures = ReadIds(raw, "pictureReferenceIds");
        string finder = DocumentJson.GetString(raw, "finder")?.Trim().ToLowerInvariant()
            ?? DefaultFinder;
        if (finder is not ("sam3" or "yolo"))
        {
            finder = DefaultFinder;
        }
        return new(
            pictures.Distinct(StringComparer.Ordinal).ToArray(),
            DocumentJson.GetOptionalBool(
                raw,
                "verifyIdentity",
                false),
            finder,
            ReadText(raw, "subject", DefaultSubject),
            ReadText(raw, "part", DefaultPart),
            Math.Clamp(subjectConfidence, MinDetectionThreshold, MaxDetectionThreshold),
            Math.Clamp(partConfidence, MinDetectionThreshold, MaxDetectionThreshold),
            ReadDimension(raw, "cropWidth", DefaultCropWidth),
            ReadDimension(raw, "cropHeight", DefaultCropHeight));
    }

    private static IReadOnlyList<string> ReadIds(JObject raw, string key) =>
        DocumentJson.GetArray(raw, key) is not JArray values
            ? []
            : values.Values<string>()
                .Select(value => value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

    private static string ReadText(
        JObject raw,
        string key,
        string fallback)
    {
        string value = DocumentJson.GetString(raw, key)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int ReadDimension(JObject raw, string key, int fallback)
    {
        double value = DocumentJson.GetOptionalDouble(raw, key, fallback, "Make Them Bonita");
        return double.IsFinite(value)
            ? Math.Clamp((int)value, MinCropDimension, MaxCropDimension)
            : fallback;
    }
}
