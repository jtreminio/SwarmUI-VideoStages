using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Parses an authored Video Stages document into its runtime specification.
/// </summary>
internal static class VideoStagesSpecParser
{
    public static VideoStagesSpec Parse(WorkflowGenerator g)
    {
        LegacyVideoSwapRequestSnapshot legacyVideoSwap = CaptureLegacyVideoSwap(g.UserInput);
        Action<string> warn =
            warning => PlanDiagnosticReporter.TrackRequestWarning(g.UserInput, warning);
        VideoStagesJsonDocument document = VideoStagesJsonReader.ReadDocument(g);
        PromptParser.VideoStageTagData tags = VideoStagesPromptSection.IsActive(g)
            ? ParseTags(g)
            : new PromptParser.VideoStageTagData();

        (int? rawWidth, int? rawHeight, int? rawFps) = PromptOverrideApplier.ApplyTopLevel(
            tags, document.Width, document.Height, document.Fps, warn);
        PromptOverrideApplier.ApplyClipAndStage(document.Entries, tags, warn);

        int width = ResolveWidth(g, rawWidth);
        int height = ResolveHeight(g, rawHeight);
        int fps = ResolveFps(g, rawFps);
        bool isTextToVideo = RootHostWorkflowFacts.IsTextToVideoRootWorkflow(g);
        bool hasConfiguredResolution = rawWidth is > 0 && rawHeight is > 0;
        if (document.Entries.Count == 0)
        {
            return new VideoStagesSpec(
                width,
                height,
                fps,
                isTextToVideo,
                [],
                hasConfiguredResolution)
            {
                LegacyVideoSwap = legacyVideoSwap,
            };
        }

        StageParserDefaults defaults = VideoStageSpecParser.BuildDefaults(g);
        VideoClipParseContext context = new(
            defaults,
            isTextToVideo,
            fps,
            tags,
            warn);

        List<ClipSpec> clips = [];
        int globalStageIndex = 0;
        for (int clipIndex = 0; clipIndex < document.Entries.Count; clipIndex++)
        {
            JObject clipObject = document.Entries[clipIndex];
            if (VideoStagesJsonReader.GetOptionalBool(clipObject, "skipped", false))
            {
                break;
            }
            if (VideoStagesJsonReader.GetArray(clipObject, "stages") is null)
            {
                VideoStagesJsonReader.Warn(
                    warn,
                    $"VideoStages: Entry {clipIndex} has no stages array and was ignored.");
                continue;
            }

            ClipSpec clip = VideoClipSpecParser.Parse(clipObject, clipIndex, context);
            if (clip.Stages.Count == 0
                && clip.InitVideo is null
                && clip.AuthoredStages.Count == 0)
            {
                continue;
            }

            List<StageSpec> activeStages = [];
            for (int clipStageIndex = 0; clipStageIndex < clip.Stages.Count; clipStageIndex++)
            {
                StageSpec stage = clip.Stages[clipStageIndex];
                activeStages.Add(stage with
                {
                    Id = globalStageIndex++,
                    ClipStageIndex = clipStageIndex,
                    // The authored stage position (including skipped stages), used by IC-LoRA targets.
                    ClipStageRawIndex = stage.Id,
                });
            }
            clips.Add(clip with { Stages = activeStages });
        }

        return new VideoStagesSpec(
            width,
            height,
            fps,
            isTextToVideo,
            clips,
            hasConfiguredResolution,
            TimelineAudioSegmentSpecParser.Parse(
                document.AudioTracks,
                document.Entries,
                warn))
        {
            LegacyVideoSwap = legacyVideoSwap,
        };
    }

    private static LegacyVideoSwapRequestSnapshot CaptureLegacyVideoSwap(T2IParamInput input)
    {
        T2IModel swapModel = input.Get(T2IParamTypes.VideoSwapModel, null);
        bool hasExplicitPercent =
            input.TryGet(T2IParamTypes.VideoSwapPercent, out double swapPercent);
        bool hasSwapSectionOverrides =
            input.SectionParamOverrides.TryGetValue(
                T2IParamInput.SectionID_VideoSwap,
                out T2IParamSet swapSection)
            && swapSection.ValuesInput.Count > 0;
        return new(
            swapModel?.Name,
            hasExplicitPercent,
            hasExplicitPercent ? swapPercent : null,
            hasSwapSectionOverrides);
    }

    private static PromptParser.VideoStageTagData ParseTags(WorkflowGenerator g) =>
        PromptParser.ExtractTagData(g.UserInput.Get(T2IParamTypes.Prompt, ""), g.UserInput);

    private static int ResolveWidth(WorkflowGenerator g, int? authoredWidth) =>
        authoredWidth is > 0 ? authoredWidth.Value : g.UserInput.GetImageWidth();

    private static int ResolveHeight(WorkflowGenerator g, int? authoredHeight) =>
        authoredHeight is > 0 ? authoredHeight.Value : g.UserInput.GetImageHeight();

    private static int ResolveFps(WorkflowGenerator g, int? authoredFps)
    {
        if (authoredFps is > 0)
        {
            return authoredFps.Value;
        }
        return g.UserInput.TryGet(T2IParamTypes.VideoFPS, out int videoFps) && videoFps > 0
            ? videoFps
            : 24;
    }
}
