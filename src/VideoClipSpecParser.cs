using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace VideoStages;

internal sealed record VideoClipParseContext(
    StageParserDefaults StageDefaults,
    bool IsTextToVideoRootWorkflow,
    int Fps,
    PromptParser.VideoStageTagData Tags,
    Action<string> Warn = null);

/// <summary>Assembles one clip from its scalar fields and the focused timeline/resource/stage parsers.</summary>
internal static class VideoClipSpecParser
{
    public static ClipSpec Parse(JObject clipObject, int clipIndex, VideoClipParseContext context)
    {
        string location = $"Clip {clipIndex}";
        double duration = VideoStagesJsonReader.GetOptionalDouble(
            clipObject, "duration", 0, location, context.Warn);
        string audioSource = NormalizeAudioSource(
            VideoStagesJsonReader.GetString(clipObject, "audioSource"));
        bool saveAudioTrack = VideoStagesJsonReader.GetOptionalBool(
            clipObject, "saveAudioTrack", false);
        bool clipLengthFromAudio = VideoStagesJsonReader.GetOptionalBool(
            clipObject, "clipLengthFromAudio", false);
        bool clipLengthFromControlNet = VideoStagesJsonReader.GetOptionalBool(
            clipObject, "clipLengthFromControlNet", false);
        bool reuseAudio = VideoStagesJsonReader.GetOptionalBool(
            clipObject, "reuseAudio", false);
        if (!double.IsFinite(duration) || duration < 0)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: {location} duration must be a finite non-negative number.");
        }
        double durationSeconds = duration;
        int? clipFrames = null;
        if (durationSeconds > 0)
        {
            try
            {
                clipFrames = ClipTimelineSpecParser.CalculateStructuralFrameCount(
                    durationSeconds,
                    context.Fps);
            }
            catch (OverflowException)
            {
                throw new SwarmUserErrorException(
                    $"VideoStages: {location} duration at {context.Fps} fps exceeds the "
                        + "supported frame range.");
            }
        }

        IReadOnlyList<IcLoraSpec> icLoras =
            VideoStageResourceParser.ParseIcLoras(clipObject, context.Warn);
        List<JObject> rawStages = VideoStagesJsonReader.GetObjectArray(clipObject, "stages");

        IReadOnlyList<ImageRefSpec> references =
            VideoStageResourceParser.ParseImageReferences(
                clipObject,
                clipIndex,
                context.Warn);
        InitVideoSpec initVideo = ClipTimelineSpecParser.ParseInitVideo(
            clipObject, duration, context.Fps, clipIndex, context.Warn);
        List<StageSpec> stages = ParseStages(
            rawStages, clipIndex, references.Count, initVideo is not null, context);
        ApplyRetake(stages, clipObject, clipIndex, duration, initVideo is not null, context);

        return new ClipSpec(
            Id: clipIndex,
            Frames: clipFrames,
            AudioSource: audioSource,
            IcLoras: icLoras,
            SaveAudioTrack: saveAudioTrack,
            ClipLengthFromAudio: clipLengthFromAudio && !clipLengthFromControlNet,
            ClipLengthFromControlNet: clipLengthFromControlNet,
            ReuseAudio: reuseAudio,
            UploadedAudio: VideoStagesJsonReader.GetEmbeddedUpload(
                clipObject, UploadContainers.ClipAudio),
            ImageRefs: references,
            Stages: stages,
            Loras: VideoStageResourceParser.ParseLoras(clipObject, context.Warn),
            PromptWindows: SortWindows(context.Tags.ClipWindows.GetValueOrDefault(clipIndex)),
            BoundaryOut: BoundaryPolicy.NormalizeAuthoredMode(
                VideoStagesJsonReader.GetString(clipObject, "boundaryOut")),
            BoundaryOutOverlap: Math.Max(
                0,
                VideoStagesJsonReader.GetOptionalInt(
                    clipObject,
                    "boundaryOutOverlap",
                    0,
                    location,
                    context.Warn)),
            InitVideo: initVideo,
            BoundaryOutCarryAudio: VideoStagesJsonReader.GetOptionalBool(
                clipObject,
                "boundaryOutCarryAudio",
                false),
            ReferenceFraming: ReferenceFraming.Parse(
                VideoStagesJsonReader.GetString(clipObject, "refFraming")))
        {
            AuthoredArchitectureHint = VideoStagesJsonReader.GetString(
                clipObject,
                "architectureHint")?.Trim().ToLowerInvariant(),
            AuthoredModelProfileHint = VideoStagesJsonReader.GetString(
                clipObject,
                "modelProfileId")?.Trim().ToLowerInvariant(),
            // Prompt-tag overrides have already been applied to rawStages by the top-level parser,
            // so architecture resolution observes exactly the authored state generation would use.
            AuthoredStages = ProjectAuthoredStages(rawStages),
        };
    }

    private static IReadOnlyList<AuthoredStageModelSpec> ProjectAuthoredStages(
        IReadOnlyList<JObject> rawStages)
    {
        int firstSkipped = -1;
        List<AuthoredStageModelSpec> authored = [];
        for (int rawIndex = 0; rawIndex < rawStages.Count; rawIndex++)
        {
            JObject stage = rawStages[rawIndex];
            if (firstSkipped < 0
                && VideoStagesJsonReader.GetOptionalBool(stage, "skipped", false))
            {
                firstSkipped = rawIndex;
            }
            authored.Add(new(
                rawIndex,
                VideoStagesJsonReader.GetString(stage, "model"),
                VideoStagesJsonReader.GetString(stage, "modelProfileId")?.Trim().ToLowerInvariant(),
                firstSkipped >= 0));
        }
        return authored;
    }

    private static List<StageSpec> ParseStages(
        IReadOnlyList<JObject> rawStages,
        int clipIndex,
        int referenceCount,
        bool initVideoClip,
        VideoClipParseContext context)
    {
        List<StageSpec> stages = [];
        for (int stageIndex = 0; stageIndex < rawStages.Count; stageIndex++)
        {
            JObject stage = rawStages[stageIndex];
            if (VideoStagesJsonReader.GetOptionalBool(stage, "skipped", false))
            {
                break;
            }
            stages.Add(VideoStageSpecParser.Parse(
                stage,
                clipIndex,
                stageIndex,
                context.StageDefaults,
                referenceCount,
                context.IsTextToVideoRootWorkflow,
                initVideoClip,
                context.Warn));
        }
        return stages;
    }

    private static void ApplyRetake(
        List<StageSpec> stages,
        JObject clipObject,
        int clipIndex,
        double duration,
        bool hasInitVideo,
        VideoClipParseContext context)
    {
        if (!hasInitVideo || stages.Count == 0)
        {
            return;
        }

        RetakeWindowSpec retake = ClipTimelineSpecParser.ParseRetake(
            clipObject, context.Fps, clipIndex, duration, context.Warn);
        if (retake is not null)
        {
            stages[^1] = stages[^1] with { RetakeWindow = retake };
        }
    }

    private static string NormalizeAudioSource(string audioSource) =>
        string.IsNullOrWhiteSpace(audioSource)
            ? Constants.AudioSourceNative
            : audioSource.Trim();

    private static IReadOnlyList<PromptWindowSpec> SortWindows(List<PromptWindowSpec> windows) =>
        windows is not { Count: > 0 } ? [] : [.. windows.OrderBy(window => window.Start)];
}
