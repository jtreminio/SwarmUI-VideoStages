using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Utils;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>Materializes embedded VideoStages media only after the parsed spec reaches runtime.</summary>
internal static class EmbeddedMediaMaterializer
{
    public static AudioFile MaterializeAudio(WorkflowGenerator g, UploadedAudioSpec spec)
    {
        return MaterializeAudio(g, spec?.Data, spec?.FileName);
    }

    /// <summary>
    /// Materializes an already-compiled media identity without returning to the parsed clip spec.
    /// </summary>
    public static AudioFile MaterializeAudio(WorkflowGenerator g, AudioMediaIdentityPlan media) =>
        MaterializeAudio(g, media?.Data, media?.FileName);

    private static AudioFile MaterializeAudio(WorkflowGenerator g, string data, string fileName)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        string material = UploadedMediaResolver.ResolveDataString(
            g, data.Trim(), "uploaded audio", StringComparison.Ordinal);
        if (material is null)
        {
            return null;
        }

        try
        {
            AudioFile audio = AudioFile.FromDataString(material);
            audio.SourceFilePath = string.IsNullOrWhiteSpace(fileName)
                ? null
                : fileName.Trim();
            return audio;
        }
        catch (Exception)
        {
            Logs.Warning("VideoStages: Ignoring invalid uploaded audio embedded in Video Stages JSON.");
            return null;
        }
    }

    /// <summary>Materializes the identity carried by a compiled sourced-clip plan.</summary>
    public static ImageFile MaterializeSourceVideo(WorkflowGenerator g, SourceVideoPlan source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Data))
        {
            return null;
        }

        string material = UploadedMediaResolver.ResolveDataString(
            g, source.Data.Trim(), "source video", StringComparison.Ordinal);
        if (material is null)
        {
            return null;
        }

        try
        {
            ImageFile video = ImageFile.FromDataString(material);
            if (video?.Type?.MetaType != MediaMetaType.Video)
            {
                Logs.Warning("VideoStages: Ignoring a clip source video whose media type is not video.");
                return null;
            }
            video.SourceFilePath = string.IsNullOrWhiteSpace(source.FileName)
                ? null
                : source.FileName.Trim();
            return video;
        }
        catch (Exception)
        {
            Logs.Warning("VideoStages: Ignoring invalid source video embedded in Video Stages JSON.");
            return null;
        }
    }
}
