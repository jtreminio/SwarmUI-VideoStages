using System.Runtime.CompilerServices;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Planning;

namespace VideoStages;

internal static class VideoStagesContext
{
    private static readonly ConditionalWeakTable<WorkflowGenerator, VideoStagesSpec> Cache = new();

    // The execution plan is deliberately a separate cache from the parsed specification so it
    // can be compiled before the host graph exists and consumed by every workflow phase.
    private static readonly ConditionalWeakTable<WorkflowGenerator, LtxPlanCacheEntry> LtxPlanCache = new();

    // Prompt-tag section resolution has no live WorkflowGenerator to key the cache on, so repeated
    // videoclip[clip,stage] lookups in one generation share the parse via the stable param input.
    private static readonly ConditionalWeakTable<T2IParamInput, VideoStagesSpec> PromptParseCache = new();

    public static VideoStagesSpec GetVideoStagesSpec(this WorkflowGenerator g) =>
        Cache.GetValue(g, VideoStagesSpecParser.Parse);

    public static VideoStagesSpec GetVideoStagesSpecForPromptParse(T2IParamInput input) =>
        PromptParseCache.GetValue(input, ParseForPromptTag);

    /// <summary>
    /// Gets the graph-free LTX execution plan compiled for this workflow, if the workflow's
    /// selected models make it an LTX timeline.
    /// </summary>
    public static LtxVideoExecutionPlanContext? GetLtxVideoExecutionPlanContext(this WorkflowGenerator g) =>
        LtxPlanCache.GetValue(g, CompileLtxPlan).Context;

    public static LtxVideoExecutionPlanContext RequireLtxVideoExecutionPlanContext(
        this WorkflowGenerator g)
    {
        LtxVideoExecutionPlanContext context = g.GetLtxVideoExecutionPlanContext();
        if (context is null)
        {
            throw new SwarmUserErrorException(
                "VideoStages currently supports LTX-Video timelines only. "
                + "WAN, mixed-model, and other video-model configurations are not supported.");
        }

        VideoPlanDiagnostic[] errors = [
            .. context.Plan.Diagnostics.Where(
                diagnostic => diagnostic.Severity == VideoPlanDiagnosticSeverity.Error)
        ];
        if (errors.Length > 0)
        {
            throw new SwarmUserErrorException(
                "VideoStages could not create a valid LTX execution plan: "
                + string.Join("; ", errors.Select(error => error.Message)));
        }
        return context;
    }

    private static VideoStagesSpec ParseForPromptTag(T2IParamInput input)
    {
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/"
        };
        return VideoStagesSpecParser.Parse(generator);
    }

    private static LtxPlanCacheEntry CompileLtxPlan(WorkflowGenerator g)
    {
        VideoStagesSpec spec = g.GetVideoStagesSpec();
        if (!IsLtxTimeline(g, spec))
        {
            return new LtxPlanCacheEntry(null);
        }

        bool canInterceptHostCore = RootVideoStageHandoff.CanInterceptHostCore(g, spec);
        RootEnvironment rootEnvironment = new(
            spec.IsTextToVideo ? HostRootKind.TextToVideoRoot : HostRootKind.ImageToVideo,
            CanHandoffHostCore: canInterceptHostCore,
            HasGlobalRefineSource: HasVideoRefineSource(g));
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(spec, rootEnvironment);
        return new LtxPlanCacheEntry(new LtxVideoExecutionPlanContext(plan));
    }

    private static bool IsLtxTimeline(WorkflowGenerator g, VideoStagesSpec spec)
    {
        // The executor has one model-family contract; one LTX stage cannot make adjacent
        // WAN/unknown stages valid.
        IReadOnlyList<StageSpec> activeStages = [.. spec.Clips.SelectMany(clip => clip.Stages)];
        if (activeStages.Count > 0)
        {
            return activeStages.All(stage => VideoStageModelCompat.IsLtxV2VideoModel(stage.Model));
        }

        // A sourced-only timeline has no stage model to inspect. It is still a valid LTX path
        // when the host's selected video model (or the main model for T2V) is LTX.
        if (!spec.Clips.Any(clip => clip.SourceVideo is not null))
        {
            return false;
        }
        T2IModel hostModel = null;
        bool hasHostModel = spec.IsTextToVideo
            ? g.UserInput.TryGet(T2IParamTypes.Model, out hostModel)
            : g.UserInput.TryGet(T2IParamTypes.VideoModel, out hostModel);
        if (!hasHostModel)
        {
            return false;
        }
        return VideoStageModelCompat.IsLtxV2VideoModel(hostModel);
    }

    private static bool HasVideoRefineSource(WorkflowGenerator g) =>
        g.UserInput.TryGet(VideoStagesExtension.RefineSourceVideo, out Image source)
        && source?.Type?.MetaType == SwarmUI.Media.MediaMetaType.Video;

    private sealed record LtxPlanCacheEntry(LtxVideoExecutionPlanContext? Context);
}

/// <summary>
/// LTX-only execution data available at every workflow phase. It is immutable and safe to inspect
/// before the native image-to-video step runs.
/// </summary>
internal sealed record LtxVideoExecutionPlanContext(
    VideoExecutionPlan Plan);
