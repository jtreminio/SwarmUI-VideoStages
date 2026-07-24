using Newtonsoft.Json.Linq;

namespace VideoStages;

internal sealed record VideoClipParseContext(
    StageParserDefaults StageDefaults,
    bool IsTextToVideoRootWorkflow,
    int Fps,
    bool RefineMode,
    int RefineSkipStages,
    PromptParser.VideoStageTagData Tags);

/// <summary>Assembles one clip from its scalar fields and the focused timeline/resource/stage parsers.</summary>
internal static class VideoClipSpecParser
{
    public static ClipSpec Parse(JObject clipObject, int clipIndex, VideoClipParseContext context)
    {
        string location = $"Clip {clipIndex}";
        double duration = VideoStagesJsonReader.GetOptionalDouble(
            clipObject, "Duration", 0, location);
        string audioSource = NormalizeAudioSource(
            VideoStagesJsonReader.GetString(clipObject, "AudioSource"));
        bool saveAudioTrack = VideoStagesJsonReader.GetOptionalBool(
            clipObject, "SaveAudioTrack", false);
        bool clipLengthFromAudio = VideoStagesJsonReader.GetOptionalBool(
            clipObject, "ClipLengthFromAudio", false);
        bool clipLengthFromControlNet = VideoStagesJsonReader.GetOptionalBool(
            clipObject, "ClipLengthFromControlNet", false);
        bool reuseAudio = VideoStagesJsonReader.GetOptionalBool(
            clipObject, "ReuseAudio", false);

        IReadOnlyList<IcLoraSpec> icLoras = VideoStageResourceParser.ParseIcLoras(clipObject);
        List<JObject> rawStages = VideoStagesJsonReader.GetObjectArray(clipObject, "Stages");

        IReadOnlyList<ImageRefSpec> references =
            VideoStageResourceParser.ParseImageReferences(clipObject, clipIndex);
        SourceVideoSpec sourceVideo = ClipTimelineSpecParser.ParseSourceVideo(
            clipObject, duration, context.Fps, clipIndex);
        List<StageSpec> stages = ParseStages(
            rawStages, clipIndex, references.Count, sourceVideo is not null, context);
        ApplyRetake(stages, clipObject, clipIndex, duration, sourceVideo is not null, context);

        double durationSeconds = Math.Max(0, duration);
        int? clipFrames = durationSeconds > 0
            ? ClipTimelineSpecParser.CalculateAlignedFrameCount(durationSeconds, context.Fps)
            : null;

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
            Loras: VideoStageResourceParser.ParseLoras(clipObject),
            PromptWindows: SortWindows(context.Tags.ClipWindows.GetValueOrDefault(clipIndex)),
            BoundaryOut: BoundaryPolicy.NormalizeAuthoredMode(
                VideoStagesJsonReader.GetString(clipObject, "BoundaryOut")),
            BoundaryOutOverlap: Math.Max(
                0,
                VideoStagesJsonReader.GetOptionalInt(
                    clipObject,
                    "BoundaryOutOverlap",
                    0,
                    location)),
            SourceVideo: sourceVideo,
            BoundaryOutCarryAudio: VideoStagesJsonReader.GetOptionalBool(
                clipObject,
                "BoundaryOutCarryAudio",
                false))
        {
            AuthoredArchitectureId = VideoStagesJsonReader.GetString(
                clipObject,
                "Architecture")?.Trim().ToLowerInvariant(),
            AuthoredModelProfileId = VideoStagesJsonReader.GetString(
                clipObject,
                "ModelProfileId")?.Trim().ToLowerInvariant(),
            // Prompt-tag overrides have already been applied to rawStages by the top-level parser,
            // so architecture resolution observes exactly the authored state generation would use.
            AuthoredStages = [.. rawStages.Select((stage, rawIndex) => new AuthoredStageModelSpec(
                rawIndex,
                VideoStagesJsonReader.GetString(stage, "Model"),
                VideoStagesJsonReader.GetString(stage, "ModelProfileId")?.Trim().ToLowerInvariant(),
                VideoStagesJsonReader.GetOptionalBool(stage, "Skipped", false)))],
        };
    }

    private static List<StageSpec> ParseStages(
        IReadOnlyList<JObject> rawStages,
        int clipIndex,
        int referenceCount,
        bool sourcedClip,
        VideoClipParseContext context)
    {
        List<StageSpec> stages = [];
        for (int stageIndex = 0; stageIndex < rawStages.Count; stageIndex++)
        {
            JObject stage = rawStages[stageIndex];
            if (VideoStagesJsonReader.GetOptionalBool(stage, "Skipped", false))
            {
                continue;
            }
            stages.Add(VideoStageSpecParser.Parse(
                stage,
                clipIndex,
                stageIndex,
                context.StageDefaults,
                referenceCount,
                context.IsTextToVideoRootWorkflow,
                context.RefineMode,
                context.RefineSkipStages,
                sourcedClip));
        }
        return stages;
    }

    private static void ApplyRetake(
        List<StageSpec> stages,
        JObject clipObject,
        int clipIndex,
        double duration,
        bool hasSourceVideo,
        VideoClipParseContext context)
    {
        if ((!context.RefineMode && !hasSourceVideo) || stages.Count == 0)
        {
            return;
        }

        RetakeWindowSpec retake = ClipTimelineSpecParser.ParseRetake(
            clipObject, context.Fps, clipIndex, duration);
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
