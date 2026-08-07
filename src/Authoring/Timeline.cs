using Newtonsoft.Json.Linq;

namespace VideoStages.Authoring;

/// <summary>
/// Reads time-based clip media and root audio spans.
/// </summary>
internal static class AuthoringTimeline
{
    private const double MinAudioLength = 0.1;
    private const double MinAudioVolume = 0.00001;
    private const double MaxAudioVolume = 100000.0;

    /// <summary>
    /// Converts authored time to an inclusive pixel-frame count without importing model policy.
    /// Architecture-grid normalization happens only after the active stage models resolve.
    /// </summary>
    public static int CalculateStructuralFrameCount(double durationSeconds, int fps)
    {
        double intervals = Math.Ceiling(durationSeconds * fps);
        if (!double.IsFinite(intervals) || intervals > int.MaxValue - 1d)
        {
            throw new OverflowException(
                "The authored duration and fps exceed the representable pixel-frame count.");
        }
        return checked((int)intervals + 1);
    }

    public static InitVideoSpec ReadInitVideo(
        JObject clipObject,
        double durationSeconds,
        int fps,
        int clipIndex,
        Action<string> warn = null)
    {
        UploadedMediaSpec upload = DocumentJson.GetEmbeddedUpload(
            clipObject, UploadContainers.ClipInitVideo);
        if (upload is null)
        {
            return null;
        }
        if (durationSeconds <= 0 || fps <= 0)
        {
            DocumentJson.Warn(
                warn,
                $"VideoStages: Clip {clipIndex} has a source video but no usable duration/fps; "
                + "generating the clip normally instead.");
            return null;
        }

        JObject container = DocumentJson.GetObject(clipObject, UploadContainers.ClipInitVideo);
        double start = DocumentJson.GetOptionalDouble(
            container, "startSeconds", 0, $"Clip {clipIndex} InitVideo", warn);
        if (!IsFinite(start) || start < 0)
        {
            start = 0;
        }
        double roundedStart = RoundTenth(start);
        if (!IsRepresentableNonNegativeFrame(Math.Round(roundedStart * fps)))
        {
            DocumentJson.Warn(
                warn,
                $"VideoStages: Clip {clipIndex} InitVideo start exceeds the representable "
                    + "frame range; ignoring the source video.");
            return null;
        }
        return new InitVideoSpec(upload.Data, upload.FileName, roundedStart);
    }

    public static RetakeWindowSpec ReadRetake(
        JObject clipObject,
        int fps,
        int clipIndex,
        double clipDurationSeconds,
        Action<string> warn = null)
    {
        JObject retake = DocumentJson.GetObject(clipObject, "retake");
        if (retake is null || fps <= 0)
        {
            return null;
        }

        string location = $"Clip {clipIndex} Retake";
        double startSeconds = DocumentJson.GetOptionalDouble(
            retake, "startSeconds", 0, location, warn);
        double lengthSeconds = DocumentJson.GetOptionalDouble(
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
            DocumentJson.Warn(
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

        double strength = DocumentJson.GetOptionalDouble(
            retake, "strength", 1.0, location, warn);
        strength = IsFinite(strength) ? Math.Clamp(strength, 0.0, 1.0) : 1.0;
        return new RetakeWindowSpec(startFrame, lengthFrames, strength);
    }

    internal static IReadOnlyList<TimelineAudioSpanSpec> ReadAudioSpans(
        IReadOnlyList<JObject> tracks,
        IReadOnlyList<JObject> clips,
        Action<string> warn = null)
    {
        // The document's own string clip id, which only a span projection refers a clip by.
        // Everything downstream carries the authored clip number this maps it to.
        Dictionary<string, int> clipIdsByDocumentId = [];
        for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
        {
            string documentId = DocumentJson.GetString(clips[clipIndex], "id")?.Trim();
            if (!string.IsNullOrWhiteSpace(documentId))
            {
                clipIdsByDocumentId.TryAdd(documentId, clipIndex);
            }
        }

        List<TimelineAudioSpanSpec> results = [];
        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            JObject track = tracks[trackIndex];
            JObject sourceObject = DocumentJson.GetObject(
                track,
                UploadContainers.AudioTrackSource);
            if (sourceObject is null)
            {
                continue;
            }

            string kind = DocumentJson.GetString(sourceObject, "kind")?.Trim();
            string reference = DocumentJson.GetString(sourceObject, "reference")?.Trim();
            UploadedMediaSpec upload = DocumentJson.GetEmbeddedUpload(
                sourceObject,
                UploadContainers.ClipAudio);
            string aceStepSource =
                StringUtils.Equals(kind, MediaSource.AceStepFun)
                && MediaSource.TryParseAceStepFunIndex(reference, out _)
                    ? reference
                    : null;
            if (upload is null && aceStepSource is null)
            {
                continue;
            }

            double rawVolume = DocumentJson.GetOptionalDouble(
                track,
                "volume",
                1,
                $"Audio track {trackIndex}",
                warn);
            double volume = double.IsFinite(rawVolume)
                ? Math.Clamp(rawVolume, MinAudioVolume, MaxAudioVolume)
                : 1;
            string rawTrackId = DocumentJson.GetString(track, "id")?.Trim();
            string trackId = string.IsNullOrWhiteSpace(rawTrackId)
                ? $"timeline-audio-{trackIndex}"
                : rawTrackId;
            List<JObject> spans = DocumentJson.GetObjectArray(track, "spans");
            for (int spanIndex = 0; spanIndex < spans.Count; spanIndex++)
            {
                JObject span = spans[spanIndex];
                double start = DocumentJson.GetOptionalDouble(
                    span,
                    "timelineStartSeconds",
                    double.NaN,
                    $"Audio track {trackIndex} span {spanIndex}",
                    warn);
                double length = DocumentJson.GetOptionalDouble(
                    span,
                    "timelineLengthSeconds",
                    double.NaN,
                    $"Audio track {trackIndex} span {spanIndex}",
                    warn);
                double sourceStart = DocumentJson.GetOptionalDouble(
                    span,
                    "sourceStartSeconds",
                    0,
                    $"Audio track {trackIndex} span {spanIndex}",
                    warn);
                if (!double.IsFinite(start)
                    || !double.IsFinite(length)
                    || !double.IsFinite(sourceStart)
                    || start < 0
                    || length < MinAudioLength
                    || sourceStart < 0)
                {
                    continue;
                }

                JObject projection = DocumentJson.GetObject(span, "projection");
                int? firstClipId = ResolveClipId(
                    projection,
                    "firstClipId",
                    clipIdsByDocumentId);
                int? lastClipId = ResolveClipId(
                    projection,
                    "lastClipId",
                    clipIdsByDocumentId);
                double? firstOffset = ReadOptionalNonNegative(
                    projection,
                    "clipStartOffsetSeconds",
                    $"Audio track {trackIndex} span {spanIndex} projection",
                    warn);
                double? lastOffset = ReadOptionalNonNegative(
                    projection,
                    "clipEndOffsetSeconds",
                    $"Audio track {trackIndex} span {spanIndex} projection",
                    warn);
                bool completeProjection =
                    firstClipId.HasValue
                    && lastClipId.HasValue
                    && firstOffset.HasValue
                    && lastOffset.HasValue;
                results.Add(new(
                    spans.Count == 1 ? trackId : $"{trackId}:{spanIndex}",
                    upload,
                    aceStepSource,
                    start,
                    sourceStart,
                    length,
                    volume,
                    completeProjection ? firstClipId : null,
                    completeProjection ? lastClipId : null,
                    completeProjection ? firstOffset : null,
                    completeProjection ? lastOffset : null));
            }
        }
        return results;
    }

    private static int? ResolveClipId(
        JObject projection,
        string key,
        IReadOnlyDictionary<string, int> clipIdsByDocumentId)
    {
        string documentId = projection is null
            ? null
            : DocumentJson.GetString(projection, key)?.Trim();
        return !string.IsNullOrWhiteSpace(documentId)
            && clipIdsByDocumentId.TryGetValue(documentId, out int clipId)
            ? clipId
            : null;
    }

    private static double? ReadOptionalNonNegative(
        JObject obj,
        string key,
        string location,
        Action<string> warn)
    {
        if (obj is null || !DocumentJson.HasProperty(obj, key))
        {
            return null;
        }
        double value = DocumentJson.GetOptionalDouble(
            obj,
            key,
            double.NaN,
            location,
            warn);
        return double.IsFinite(value) && value >= 0 ? value : null;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsRepresentableNonNegativeFrame(double value) =>
        IsFinite(value) && value >= 0 && value <= int.MaxValue;

    private static double RoundTenth(double value) =>
        Math.Round(value * 10) / 10;

}
