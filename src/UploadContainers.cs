namespace VideoStages;

/// <summary>The embedded-upload containers a clip can carry. Each container property holds a base64
/// "data" blob that <see cref="MetadataSanitizer"/> strips; the parser reads the same containers.
/// Both derive their property names from here so adding an upload field is a single edit.</summary>
internal static class UploadContainers
{
    public const string ClipAudio = "uploadedAudio";
    public const string ClipSourceVideo = "sourceVideo";
    public const string IcLoraDriveMedia = "driveMedia";
    public const string RefImage = "uploadedImage";

    public const string IcLorasCollection = "icLoras";
    public const string RefsCollection = "refs";

    /// <summary>(Collection, Container): the container holds the "data" blob. A null Collection means
    /// the container sits directly on the clip, otherwise on each element of that array.</summary>
    public static readonly (string Collection, string Container)[] All =
    [
        (null, ClipAudio),
        (null, ClipSourceVideo),
        (IcLorasCollection, IcLoraDriveMedia),
        (RefsCollection, RefImage),
    ];
}
