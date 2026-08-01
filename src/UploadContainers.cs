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

/// <summary>Shared keys and traversal paths for parsing and sanitizing embedded uploads.</summary>
internal static class UploadContainers
{
    /// <summary>Property names are part of the frontend authoring-document contract.</summary>
    public const string ClipAudio = "uploadedAudio";
    public const string ClipInitVideo = "initVideo";
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
        new([UploadPathStep.Each(ClipsCollection)], ClipInitVideo),
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
