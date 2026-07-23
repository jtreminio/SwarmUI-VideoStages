namespace VideoStages.Planning;

/// <summary>Builds the media values shared by independent audio planning components.</summary>
internal static class AudioMediaIdentityCompiler
{
    internal static AudioMediaIdentityPlan Compile(UploadedMediaSpec media) => media is null
        ? null
        : new AudioMediaIdentityPlan(media.Data, media.FileName);

}
