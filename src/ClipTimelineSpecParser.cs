using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>
/// Parses time-based clip media: source-video ranges, retake windows, and additive audio segments.
/// </summary>
internal static class ClipTimelineSpecParser
{
    /// <summary>
    /// Converts authored time to an inclusive pixel-frame count without importing model policy.
    /// Architecture-grid normalization happens only after the active stage models resolve.
    /// </summary>
    public static int CalculateStructuralFrameCount(double durationSeconds, int fps)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                durationSeconds,
                "An authored duration must be finite and non-negative.");
        }
        if (fps < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fps),
                fps,
                "Timeline fps must be positive.");
        }
        double intervals = Math.Ceiling(durationSeconds * fps);
        if (!double.IsFinite(intervals) || intervals > int.MaxValue - 1d)
        {
            throw new OverflowException(
                "The authored duration and fps exceed the representable pixel-frame count.");
        }
        return checked((int)intervals + 1);
    }

    public static SourceVideoSpec ParseSourceVideo(
        JObject clipObject,
        double durationSeconds,
        int fps,
        int clipIndex,
        Action<string> warn = null)
    {
        UploadedMediaSpec upload = VideoStagesJsonReader.GetEmbeddedUpload(
            clipObject, UploadContainers.ClipSourceVideo);
        if (upload is null)
        {
            return null;
        }
        if (durationSeconds <= 0 || fps <= 0)
        {
            VideoStagesJsonReader.Warn(
                warn,
                $"VideoStages: Clip {clipIndex} has a source video but no usable duration/fps; "
                + "generating the clip normally instead.");
            return null;
        }

        JObject container = VideoStagesJsonReader.GetObject(clipObject, UploadContainers.ClipSourceVideo);
        double start = VideoStagesJsonReader.GetOptionalDouble(
            container, "startSeconds", 0, $"Clip {clipIndex} SourceVideo", warn);
        if (!IsFinite(start) || start < 0)
        {
            start = 0;
        }
        double roundedStart = RoundTenth(start);
        if (!IsRepresentableNonNegativeFrame(Math.Round(roundedStart * fps)))
        {
            throw new SwarmUserErrorException(
                $"VideoStages: Clip {clipIndex} SourceVideo start exceeds the representable "
                    + "frame range.");
        }
        return new SourceVideoSpec(upload.Data, upload.FileName, roundedStart);
    }

    public static RetakeWindowSpec ParseRetake(
        JObject clipObject,
        int fps,
        int clipIndex,
        double clipDurationSeconds,
        Action<string> warn = null)
    {
        JObject retake = VideoStagesJsonReader.GetObject(clipObject, "retake");
        if (retake is null || fps <= 0)
        {
            return null;
        }

        string location = $"Clip {clipIndex} Retake";
        double startSeconds = VideoStagesJsonReader.GetOptionalDouble(
            retake, "startSeconds", 0, location, warn);
        double lengthSeconds = VideoStagesJsonReader.GetOptionalDouble(
            retake, "lengthSeconds", 0, location, warn);
        if (!IsFinite(startSeconds) || !IsFinite(lengthSeconds)
            || startSeconds < 0 || lengthSeconds <= 0)
        {
            return null;
        }

        double rawStartFrame = Math.Round(startSeconds * fps);
        double rawLengthFrames = Math.Round(lengthSeconds * fps);
        if (!IsRepresentableNonNegativeFrame(rawStartFrame)
            || !IsRepresentableNonNegativeFrame(rawLengthFrames))
        {
            VideoStagesJsonReader.Warn(
                warn,
                $"VideoStages: {location} exceeds the representable frame range and was ignored.");
            return null;
        }
        int startFrame = (int)rawStartFrame;
        int lengthFrames = (int)rawLengthFrames;
        if (clipDurationSeconds > 0
            && startSeconds + lengthSeconds >= clipDurationSeconds - 0.5 / fps)
        {
            lengthFrames = Math.Max(
                lengthFrames,
                CalculateStructuralFrameCount(clipDurationSeconds, fps) - startFrame);
        }
        if (startFrame < 0 || lengthFrames <= 0)
        {
            return null;
        }

        double strength = VideoStagesJsonReader.GetOptionalDouble(
            retake, "strength", 1.0, location, warn);
        strength = IsFinite(strength) ? Math.Clamp(strength, 0.0, 1.0) : 1.0;
        return new RetakeWindowSpec(startFrame, lengthFrames, strength);
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsRepresentableNonNegativeFrame(double value) =>
        IsFinite(value) && value >= 0 && value <= int.MaxValue;

    private static double RoundTenth(double value) =>
        Math.Round(value * 10) / 10;

}
