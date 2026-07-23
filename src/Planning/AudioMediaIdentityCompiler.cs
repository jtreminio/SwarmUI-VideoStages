namespace VideoStages.Planning;

/// <summary>Builds the media values shared by independent audio planning components.</summary>
internal static class AudioMediaIdentityCompiler
{
    internal static AudioMediaIdentityPlan Compile(UploadedAudioSpec media) => media is null
        ? null
        : new AudioMediaIdentityPlan(media.Data, media.FileName);

    internal static IcLoraUploadedMediaKind CompileIcLoraMediaKind(string data)
    {
        if (data?.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return IcLoraUploadedMediaKind.Image;
        }
        if (data?.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return IcLoraUploadedMediaKind.Video;
        }
        return string.IsNullOrWhiteSpace(data)
            ? IcLoraUploadedMediaKind.None
            : IcLoraUploadedMediaKind.Unknown;
    }
}
