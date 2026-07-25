namespace VideoStages;

/// <summary>One step on the way to an upload container: a named property that is either an object
/// hop or an array whose every element is walked.</summary>
internal readonly record struct UploadPathStep(string Name, bool IsArray)
{
    internal static UploadPathStep Each(string name) => new(name, IsArray: true);

    internal static UploadPathStep Into(string name) => new(name, IsArray: false);
}

/// <summary>A full document path to an upload container, rooted at the authoring document.</summary>
internal sealed record UploadContainerPath(
    IReadOnlyList<UploadPathStep> Steps,
    string Container);

/// <summary>The embedded-upload containers an authoring document can carry. Each container property
/// holds a base64 "data" blob that <see cref="MetadataSanitizer"/> strips; the parsers read the same
/// containers. Both derive their property names from here, and the sanitizer walks
/// <see cref="AllPaths"/> rather than a hardcoded traversal, so adding an upload field is a single
/// edit that cannot be missed by output metadata.</summary>
internal static class UploadContainers
{
    public const string ClipAudio = "uploadedAudio";
    public const string ClipSourceVideo = "sourceVideo";
    public const string IcLoraDriveMedia = "driveMedia";
    public const string RefImage = "uploadedImage";

    public const string ClipsCollection = "clips";
    public const string IcLorasCollection = "icLoras";
    public const string RefsCollection = "refs";
    public const string AudioTracksCollection = "audioTracks";
    public const string AudioTrackSource = "source";

    public static readonly IReadOnlyList<UploadContainerPath> AllPaths =
    [
        new([UploadPathStep.Each(ClipsCollection)], ClipAudio),
        new([UploadPathStep.Each(ClipsCollection)], ClipSourceVideo),
        new(
            [UploadPathStep.Each(ClipsCollection), UploadPathStep.Each(IcLorasCollection)],
            IcLoraDriveMedia),
        new(
            [UploadPathStep.Each(ClipsCollection), UploadPathStep.Each(RefsCollection)],
            RefImage),
        new(
            [UploadPathStep.Each(AudioTracksCollection), UploadPathStep.Into(AudioTrackSource)],
            ClipAudio),
    ];
}
