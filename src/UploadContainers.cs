namespace VideoStages;

/// <summary>Authoring-document property names shared by the readers and the metadata sanitizer.</summary>
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
}
