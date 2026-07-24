using Newtonsoft.Json.Linq;

namespace VideoStages;

/// <summary>
/// Parses the root-authored audio lanes. The frontend writes one timeline window per logical
/// segment; accepting every valid span keeps previously authored planned-track documents useful.
/// Source-less rows remain editable in the browser but are omitted from generation.
/// </summary>
internal static class TimelineAudioSegmentSpecParser
{
    private const double MinLength = 0.1;
    private const double MinVolume = 0.00001;
    private const double MaxVolume = 100000.0;

    internal static IReadOnlyList<TimelineAudioSegmentSpec> Parse(
        IReadOnlyList<JObject> tracks,
        IReadOnlyList<JObject> clips)
    {
        Dictionary<string, int> clipIds = [];
        for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
        {
            string clipId = VideoStagesJsonReader.GetString(clips[clipIndex], "Id")?.Trim();
            if (!string.IsNullOrWhiteSpace(clipId))
            {
                clipIds.TryAdd(clipId, clipIndex);
            }
        }
        List<TimelineAudioSegmentSpec> segments = [];
        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            JObject track = tracks[trackIndex];
            JObject sourceObject = VideoStagesJsonReader.GetObject(track, "Source");
            if (sourceObject is null)
            {
                continue;
            }

            string kind = VideoStagesJsonReader.GetString(sourceObject, "Kind")?.Trim();
            string reference = VideoStagesJsonReader.GetString(sourceObject, "Reference")?.Trim();
            UploadedMediaSpec upload = VideoStagesJsonReader.GetEmbeddedUpload(
                sourceObject,
                "UploadedAudio");
            string aceStepSource =
                string.Equals(kind, "AceStepFun", StringComparison.OrdinalIgnoreCase)
                && AudioHandler.TryParseAceStepFunAudioSource(reference, out _)
                    ? reference
                    : null;
            if (upload is null && aceStepSource is null)
            {
                continue;
            }

            double rawVolume = VideoStagesJsonReader.GetOptionalDouble(
                track,
                "Volume",
                1,
                $"Audio track {trackIndex}");
            double volume = double.IsFinite(rawVolume)
                ? Math.Clamp(rawVolume, MinVolume, MaxVolume)
                : 1;
            string rawTrackId = VideoStagesJsonReader.GetString(track, "Id")?.Trim();
            string trackId = string.IsNullOrWhiteSpace(rawTrackId)
                ? $"timeline-audio-{trackIndex}"
                : rawTrackId;
            List<JObject> spans = VideoStagesJsonReader.GetObjectArray(track, "Spans");
            for (int spanIndex = 0; spanIndex < spans.Count; spanIndex++)
            {
                JObject span = spans[spanIndex];
                double start = VideoStagesJsonReader.GetOptionalDouble(
                    span,
                    "TimelineStartSeconds",
                    double.NaN,
                    $"Audio track {trackIndex} span {spanIndex}");
                double length = VideoStagesJsonReader.GetOptionalDouble(
                    span,
                    "TimelineLengthSeconds",
                    double.NaN,
                    $"Audio track {trackIndex} span {spanIndex}");
                double sourceStart = VideoStagesJsonReader.GetOptionalDouble(
                    span,
                    "SourceStartSeconds",
                    0,
                    $"Audio track {trackIndex} span {spanIndex}");
                if (!double.IsFinite(start)
                    || !double.IsFinite(length)
                    || !double.IsFinite(sourceStart)
                    || start < 0
                    || length < MinLength
                    || sourceStart < 0)
                {
                    continue;
                }

                JObject projection = VideoStagesJsonReader.GetObject(span, "Projection");
                int? firstClipId = ResolveClipId(
                    projection,
                    "FirstClipId",
                    clipIds);
                int? lastClipId = ResolveClipId(
                    projection,
                    "LastClipId",
                    clipIds);
                double? firstOffset = ReadOptionalNonNegative(
                    projection,
                    "ClipStartOffsetSeconds",
                    $"Audio track {trackIndex} span {spanIndex} projection");
                double? lastOffset = ReadOptionalNonNegative(
                    projection,
                    "ClipEndOffsetSeconds",
                    $"Audio track {trackIndex} span {spanIndex} projection");
                bool completeProjection =
                    firstClipId.HasValue
                    && lastClipId.HasValue
                    && firstOffset.HasValue
                    && lastOffset.HasValue;
                segments.Add(new(
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
        return segments;
    }

    private static int? ResolveClipId(
        JObject projection,
        string key,
        IReadOnlyDictionary<string, int> clipIds)
    {
        string id = projection is null
            ? null
            : VideoStagesJsonReader.GetString(projection, key)?.Trim();
        return !string.IsNullOrWhiteSpace(id) && clipIds.TryGetValue(id, out int clipId)
            ? clipId
            : null;
    }

    private static double? ReadOptionalNonNegative(
        JObject obj,
        string key,
        string location)
    {
        if (obj is null || !VideoStagesJsonReader.HasProperty(obj, key))
        {
            return null;
        }
        double value = VideoStagesJsonReader.GetOptionalDouble(
            obj,
            key,
            double.NaN,
            location);
        return double.IsFinite(value) && value >= 0 ? value : null;
    }
}
