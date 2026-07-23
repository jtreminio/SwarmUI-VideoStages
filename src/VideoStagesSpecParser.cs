using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>
/// Compiles the authored Video Stages document into its runtime specification. Detailed parsing is
/// delegated to focused readers; this type remains the compatibility entry point used by the host.
/// </summary>
internal static class VideoStagesSpecParser
{
    public static VideoStagesSpec Parse(WorkflowGenerator g)
    {
        VideoStagesJsonDocument document = VideoStagesJsonReader.ReadDocument(g);
        PromptParser.VideoStageTagData tags = VideoStagesPromptSection.IsActive(g)
            ? ParseTags(g)
            : new PromptParser.VideoStageTagData();

        (int? rawWidth, int? rawHeight, int? rawFps) = PromptOverrideApplier.ApplyTopLevel(
            tags, document.Width, document.Height, document.Fps);
        PromptOverrideApplier.ApplyClipAndStage(document.Entries, tags);

        int width = ResolveWidth(g, rawWidth);
        int height = ResolveHeight(g, rawHeight);
        int fps = ResolveFps(g, rawFps);
        bool isTextToVideo = RootVideoStageHandoff.IsTextToVideoRootWorkflow(g);
        bool refineMode = VideoStagesGate.IsRefineSourceVideoMode(g);
        int refineSkipStages = VideoStagesGate.ResolveRefineSkipStages(g, refineMode);
        bool hasConfiguredResolution = rawWidth is > 0 && rawHeight is > 0;
        if (document.Entries.Count == 0)
        {
            return new VideoStagesSpec(
                width,
                height,
                fps,
                isTextToVideo,
                [],
                hasConfiguredResolution);
        }

        ValidateClipShapes(document.Entries);
        StageParserDefaults defaults = VideoStageSpecParser.BuildDefaults(g);
        VideoClipParseContext context = new(
            defaults,
            isTextToVideo,
            fps,
            refineMode,
            refineSkipStages,
            tags);

        List<ClipSpec> clips = [];
        int globalStageIndex = 0;
        for (int clipIndex = 0; clipIndex < document.Entries.Count; clipIndex++)
        {
            JObject clipObject = document.Entries[clipIndex];
            if (VideoStagesJsonReader.GetOptionalBool(clipObject, "Skipped", false))
            {
                continue;
            }

            ClipSpec clip = VideoClipSpecParser.Parse(clipObject, clipIndex, context);
            if (clip.Stages.Count == 0 && clip.SourceVideo is null)
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
            hasConfiguredResolution);
    }

    private static PromptParser.VideoStageTagData ParseTags(WorkflowGenerator g) =>
        PromptParser.ExtractTagData(g.UserInput.Get(T2IParamTypes.Prompt, ""), g.UserInput);

    private static void ValidateClipShapes(IReadOnlyList<JObject> entries)
    {
        for (int index = 0; index < entries.Count; index++)
        {
            if (!VideoStagesJsonReader.HasProperty(entries[index], "Stages"))
            {
                throw new SwarmUserErrorException(
                    $"VideoStages: Entry {index} is not a clip object (must have a 'Stages' array).");
            }
        }
    }

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
