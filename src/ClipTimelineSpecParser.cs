using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>
/// Parses time-based clip media: source-video ranges, retake windows, and additive audio segments.
/// </summary>
internal static class ClipTimelineSpecParser
{
    private const int FrameAlignment = 8;

    public static int CalculateAlignedFrameCount(double durationSeconds, int fps)
    {
        int rawFrames = Math.Max(0, (int)Math.Ceiling(durationSeconds * fps));
        int alignedFrames = (int)Math.Ceiling(rawFrames / (double)FrameAlignment) * FrameAlignment;
        return Math.Max(1, alignedFrames + 1);
    }

    public static SourceVideoSpec ParseSourceVideo(
        JObject clipObject,
        double durationSeconds,
        int fps,
        int clipIndex)
    {
        UploadedMediaSpec upload = VideoStagesJsonReader.GetEmbeddedUpload(
            clipObject, UploadContainers.ClipSourceVideo);
        if (upload is null)
        {
            return null;
        }
        if (durationSeconds <= 0 || fps <= 0)
        {
            Logs.Warning(
                $"VideoStages: Clip {clipIndex} has a source video but no usable duration/fps; "
                + "generating the clip normally instead.");
            return null;
        }

        JObject container = VideoStagesJsonReader.GetObject(clipObject, UploadContainers.ClipSourceVideo);
        double start = VideoStagesJsonReader.GetOptionalDouble(
            container, "StartSeconds", 0, $"Clip {clipIndex} SourceVideo");
        if (!IsFinite(start) || start < 0)
        {
            start = 0;
        }
        return new SourceVideoSpec(upload.Data, upload.FileName, RoundTenth(start));
    }

    public static RetakeWindowSpec ParseRetake(
        JObject clipObject,
        int fps,
        int clipIndex,
        double clipDurationSeconds)
    {
        JObject retake = VideoStagesJsonReader.GetObject(clipObject, "Retake");
        if (retake is null || fps <= 0)
        {
            return null;
        }

        string location = $"Clip {clipIndex} Retake";
        double startSeconds = VideoStagesJsonReader.GetOptionalDouble(retake, "StartSeconds", 0, location);
        double lengthSeconds = VideoStagesJsonReader.GetOptionalDouble(retake, "LengthSeconds", 0, location);
        if (!IsFinite(startSeconds) || !IsFinite(lengthSeconds)
            || startSeconds < 0 || lengthSeconds <= 0)
        {
            return null;
        }

        int startFrame = (int)Math.Round(startSeconds * fps);
        int lengthFrames = (int)Math.Round(lengthSeconds * fps);
        if (clipDurationSeconds > 0
            && startSeconds + lengthSeconds >= clipDurationSeconds - 0.5 / fps)
        {
            lengthFrames = Math.Max(
                lengthFrames,
                CalculateAlignedFrameCount(clipDurationSeconds, fps) - startFrame);
        }
        if (startFrame < 0 || lengthFrames <= 0)
        {
            return null;
        }

        double strength = VideoStagesJsonReader.GetOptionalDouble(retake, "Strength", 1.0, location);
        strength = IsFinite(strength) ? Math.Clamp(strength, 0.0, 1.0) : 1.0;
        return new RetakeWindowSpec(startFrame, lengthFrames, strength);
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static double RoundTenth(double value) =>
        Math.Round(value * 10) / 10;
}
