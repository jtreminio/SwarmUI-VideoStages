using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Loads the media a timeline uploaded — carried inline as a data string, or left on the server as
/// an <c>inputs/</c>, <c>raw/</c>, or <c>Starred/</c> path — and only once the parsed spec reaches
/// runtime. Every failure mode is proven absent by <see cref="UploadedMediaPreflight"/> before graph
/// mutation begins, so the throwing entry points treat one here as a bug, not authored input.
/// </summary>
internal static class UploadedMedia
{
    public static AudioFile GetAudio(T2IParamInput input, UploadedMediaSpec spec) =>
        GetAudio(input, spec?.Data, spec?.FileName);

    public static AudioFile GetAudio(T2IParamInput input, AudioMediaIdentityPlan media) =>
        GetAudio(input, media?.Data, media?.FileName);

    private static AudioFile GetAudio(T2IParamInput input, string data, string fileName)
    {
        if (!TryGetAudio(input, data, fileName, out AudioFile audio, out string error))
        {
            throw Invariant.Failure(
                $"Request preflight accepted uploaded audio that runtime cannot load. {error}");
        }
        return audio;
    }

    public static ImageFile GetInitVideo(T2IParamInput input, InitVideoPlan source)
    {
        if (!TryGetInitVideo(input, source, out ImageFile video, out string error))
        {
            throw Invariant.Failure(
                $"Request preflight accepted a clip source video that runtime cannot load. {error}");
        }
        return video;
    }

    public static ImageFile GetRefImage(
        T2IParamInput input,
        string inlineData,
        string uploadFileName,
        string descriptor)
    {
        if (!TryGetRefImage(
            input, inlineData, uploadFileName, descriptor, out ImageFile image, out string error))
        {
            throw Invariant.Failure(
                $"Request preflight accepted a reference image that runtime cannot load. {error}");
        }
        return image;
    }

    internal static bool TryGetAudio(
        T2IParamInput input,
        string data,
        string fileName,
        out AudioFile audio,
        out string error)
    {
        audio = null;
        if (string.IsNullOrWhiteSpace(data))
        {
            error = null;
            return true;
        }
        if (!TryResolveDataString(
            input, data.Trim(), "uploaded audio", StringComparison.Ordinal, out string material, out error))
        {
            return false;
        }
        try
        {
            audio = AudioFile.FromDataString(material);
        }
        catch (Exception ex)
        {
            error = $"The uploaded audio embedded in Video Stages JSON is not readable. {ex.Message}";
            return false;
        }
        audio.SourceFilePath = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim();
        return true;
    }

    internal static bool TryGetInitVideo(
        T2IParamInput input,
        InitVideoPlan source,
        out ImageFile video,
        out string error)
    {
        video = null;
        if (source is null || string.IsNullOrWhiteSpace(source.Data))
        {
            error = null;
            return true;
        }
        if (!TryResolveDataString(
            input, source.Data.Trim(), "source video", StringComparison.Ordinal, out string material, out error))
        {
            return false;
        }
        try
        {
            video = ImageFile.FromDataString(material);
        }
        catch (Exception ex)
        {
            error = $"The clip source video embedded in Video Stages JSON is not readable. {ex.Message}";
            return false;
        }
        if (video?.Type?.MetaType != MediaMetaType.Video)
        {
            video = null;
            error = "A clip source video is not a video file "
                + $"(media type '{(object)video?.Type?.MetaType?.Name ?? "unknown"}').";
            return false;
        }
        video.SourceFilePath = string.IsNullOrWhiteSpace(source.FileName)
            ? null
            : source.FileName.Trim();
        return true;
    }

    internal static bool TryGetRefImage(
        T2IParamInput input,
        string inlineData,
        string uploadFileName,
        string descriptor,
        out ImageFile image,
        out string error)
    {
        image = null;
        string material = inlineData ?? uploadFileName;
        if (string.IsNullOrEmpty(material))
        {
            error = $"Upload {descriptor} is missing inline data and a file name.";
            return false;
        }
        if (!TryResolveDataString(
            input, material, descriptor, StringComparison.OrdinalIgnoreCase, out material, out error))
        {
            return false;
        }
        try
        {
            image = ImageFile.FromDataString(material);
        }
        catch (Exception ex)
        {
            error = $"The {descriptor} payload is not a readable image. {ex.Message}";
            return false;
        }
        return true;
    }

    private static bool TryResolveDataString(
        T2IParamInput input,
        string material,
        string descriptor,
        StringComparison pathComparison,
        out string data,
        out string error)
    {
        error = null;
        data = material;
        if (!material.StartsWith("inputs/", pathComparison)
            && !material.StartsWith("raw/", pathComparison)
            && !material.StartsWith("Starred/", pathComparison))
        {
            return true;
        }

        if (input?.SourceSession is null)
        {
            data = null;
            error = $"{descriptor} uses a server-side path (inputs/, raw/, or Starred/) "
                + "but no session is available; cannot load the file.";
            return false;
        }

        try
        {
            data = T2IParamTypes.FilePathToDataString(
                input.SourceSession,
                material,
                $"for VideoStages {descriptor}");
            return true;
        }
        catch (SwarmReadableErrorException ex)
        {
            data = null;
            error = $"Could not resolve uploaded {descriptor} path '{material}': {ex.Message}";
            return false;
        }
    }
}
