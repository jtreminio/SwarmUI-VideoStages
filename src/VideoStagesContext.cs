using System.Runtime.CompilerServices;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Planning;

namespace VideoStages;

internal static class VideoStagesContext
{
    private static readonly ConditionalWeakTable<WorkflowGenerator, VideoStagesSpec> Cache = new();

    // The execution plan is deliberately a separate cache from the parsed specification so it
    // can be compiled before the host graph exists and consumed by every workflow phase.
    private static readonly ConditionalWeakTable<WorkflowGenerator, PlanCacheEntry> PlanCache = new();

    // Prompt-tag section resolution has no live WorkflowGenerator to key the cache on, so repeated
    // videoclip[clip,stage] lookups in one generation share the parse via the stable param input.
    private static readonly ConditionalWeakTable<T2IParamInput, VideoStagesSpec> PromptParseCache = new();

    public static VideoStagesSpec GetVideoStagesSpec(this WorkflowGenerator g) =>
        Cache.GetValue(g, VideoStagesSpecParser.Parse);

    public static VideoStagesSpec GetVideoStagesSpecForPromptParse(T2IParamInput input) =>
        PromptParseCache.GetValue(input, ParseForPromptTag);

    /// <summary>
    /// Gets the graph-free architecture-resolved execution plan compiled for this workflow.
    /// </summary>
    public static VideoExecutionPlanContext? GetVideoExecutionPlanContext(this WorkflowGenerator g) =>
        PlanCache.GetValue(g, CompilePlan).Context;

    public static VideoExecutionPlanContext RequireVideoExecutionPlanContext(
        this WorkflowGenerator g)
    {
        VideoExecutionPlanContext context = g.GetVideoExecutionPlanContext();
        if (context is null)
        {
            throw new SwarmUserErrorException(
                "VideoStages has no executable clips in the active timeline.");
        }

        VideoPlanDiagnostic[] errors = [
            .. context.Plan.Diagnostics.Where(
                diagnostic => diagnostic.Severity == VideoPlanDiagnosticSeverity.Error)
        ];
        if (errors.Length > 0)
        {
            throw new SwarmUserErrorException(
                "VideoStages could not create a valid architecture execution plan: "
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

    private static PlanCacheEntry CompilePlan(WorkflowGenerator g)
    {
        VideoStagesSpec spec = g.GetVideoStagesSpec();
        if (spec.Clips.Count == 0)
        {
            return new PlanCacheEntry(null);
        }
        ArchitecturePlanningResult architecturePlanning =
            ArchitecturePlanResolver.Resolve(spec, VideoArchitectureRegistry.Production);

        bool canInterceptHostCore = RootHostWorkflowFacts.CanInterceptHostCore(g, spec);
        RootEnvironment rootEnvironment = new(
            spec.IsTextToVideo ? HostRootKind.TextToVideoRoot : HostRootKind.ImageToVideo,
            CanHandoffHostCore: canInterceptHostCore,
            HasGlobalRefineSource: HasVideoRefineSource(g));
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            rootEnvironment,
            architecturePlanning);
        return new PlanCacheEntry(new VideoExecutionPlanContext(plan));
    }

    private static bool HasVideoRefineSource(WorkflowGenerator g) =>
        g.UserInput.TryGet(VideoStagesExtension.RefineSourceVideo, out Image source)
        && source?.Type?.MetaType == SwarmUI.Media.MediaMetaType.Video;

    private sealed record PlanCacheEntry(VideoExecutionPlanContext? Context);
}

/// <summary>
/// Architecture-resolved execution data available at every workflow phase.
/// </summary>
internal sealed record VideoExecutionPlanContext(
    VideoExecutionPlan Plan);
