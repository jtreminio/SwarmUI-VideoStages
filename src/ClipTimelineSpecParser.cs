using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>
/// Parses time-based clip media: source-video ranges, retake windows, and additive audio segments.
/// </summary>
internal static class ClipTimelineSpecParser
{
    private const int FrameAlignment = 8;
    private const double AudioSegmentMinLength = 0.1;

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
        UploadedAudioSpec upload = VideoStagesJsonReader.GetEmbeddedUpload(
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

    public static IReadOnlyList<AudioSegmentSpec> ParseAudioSegments(
        JObject clipObject,
        double clipDurationSeconds)
    {
        List<JObject> rawSegments = VideoStagesJsonReader.GetObjectArray(
            clipObject, UploadContainers.SegmentsCollection);
        if (rawSegments.Count == 0)
        {
            return [];
        }

        List<AudioSegmentSpec> segments = [];
        foreach (JObject segmentObject in rawSegments)
        {
            UploadedAudioSpec source = VideoStagesJsonReader.GetEmbeddedUpload(
                segmentObject, UploadContainers.SegmentSource);
            string aceSource = null;
            if (source is null)
            {
                string sourceRef = VideoStagesJsonReader.GetString(segmentObject, "Source")?.Trim();
                if (!string.IsNullOrEmpty(sourceRef)
                    && AudioHandler.TryParseAceStepFunAudioSource(sourceRef, out _))
                {
                    aceSource = sourceRef;
                }
            }
            if (source is null && aceSource is null)
            {
                continue;
            }

            double start = VideoStagesJsonReader.GetOptionalDouble(
                segmentObject, "StartSeconds", 0, "Clip AudioSegment");
            double trim = VideoStagesJsonReader.GetOptionalDouble(
                segmentObject, "TrimStartSeconds", 0, "Clip AudioSegment");
            double length = VideoStagesJsonReader.GetOptionalDouble(
                segmentObject, "LengthSeconds", 0, "Clip AudioSegment");
            if (!IsFinite(start) || !IsFinite(trim) || !IsFinite(length) || length <= 0)
            {
                continue;
            }

            start = Math.Max(0, start);
            trim = Math.Max(0, trim);
            if (clipDurationSeconds > 0)
            {
                double maxStart = Math.Max(0, clipDurationSeconds - AudioSegmentMinLength);
                start = Math.Min(start, maxStart);
                length = Math.Clamp(
                    length,
                    AudioSegmentMinLength,
                    Math.Max(AudioSegmentMinLength, clipDurationSeconds - start));
            }

            segments.Add(new AudioSegmentSpec(
                source,
                RoundTenth(start),
                RoundTenth(trim),
                RoundTenth(length),
                aceSource));
        }
        segments.Sort((left, right) => left.StartSeconds.CompareTo(right.StartSeconds));
        return segments;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static double RoundTenth(double value) =>
        Math.Round(value * 10) / 10;
}
