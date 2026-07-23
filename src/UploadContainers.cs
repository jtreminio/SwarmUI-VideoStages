namespace VideoStages;

/// <summary>The embedded-upload containers a clip can carry. Each container property holds a base64
/// "Data" blob that <see cref="MetadataSanitizer"/> strips; the parser reads the same containers.
/// Both derive their property names from here so adding an upload field is a single edit.</summary>
internal static class UploadContainers
{
    public const string ClipAudio = "UploadedAudio";
    public const string ClipSourceVideo = "SourceVideo";
    public const string SegmentSource = "Source";
    public const string IcLoraDriveMedia = "DriveMedia";
    public const string RefImage = "UploadedImage";

    public const string SegmentsCollection = "AudioSegments";
    public const string IcLorasCollection = "IcLoras";
    public const string RefsCollection = "Refs";

    /// <summary>(Collection, Container): the container holds the "Data" blob. A null Collection means
    /// the container sits directly on the clip, otherwise on each element of that array.</summary>
    public static readonly (string Collection, string Container)[] All =
    [
        (null, ClipAudio),
        (null, ClipSourceVideo),
        (SegmentsCollection, SegmentSource),
        (IcLorasCollection, IcLoraDriveMedia),
        (RefsCollection, RefImage),
    ];
}
