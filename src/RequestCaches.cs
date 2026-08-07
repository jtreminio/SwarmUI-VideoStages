using System.Runtime.CompilerServices;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages;

internal static class RequestCaches
{
    private static readonly ConditionalWeakTable<WorkflowGenerator, TimelineSpec> Cache = new();

    // The execution plan is deliberately a separate cache from the parsed specification so it
    // can be compiled before the host graph exists and consumed by every workflow phase.
    private static readonly ConditionalWeakTable<WorkflowGenerator, VideoExecutionPlanContext> PlanCache = new();

    // Prompt-tag section resolution has no live WorkflowGenerator to key the cache on, so repeated
    // videoclip[clip,stage] lookups in one generation share the parse via the stable param input.
    private static readonly ConditionalWeakTable<T2IParamInput, TimelineSpec> PromptParseCache = new();

    public static TimelineSpec GetTimelineSpec(this WorkflowGenerator g) =>
        Cache.GetValue(g, generator => RequestReader.Read(generator.UserInput));

    public static TimelineSpec GetTimelineSpecForPromptParse(T2IParamInput input) =>
        PromptParseCache.GetValue(input, RequestReader.Read);

    public static VideoExecutionPlanContext? GetVideoExecutionPlanContext(this WorkflowGenerator g) =>
        PlanCache.GetValue(g, CompilePlan);

    public static VideoExecutionPlanContext RequireVideoExecutionPlanContext(
        this WorkflowGenerator g)
    {
        VideoExecutionPlanContext context = RequireExistingContext(g);
        PlanDiagnosticReporter.ThrowIfBlocking(
            context.Plan.Diagnostics,
            "VideoStages could not create a valid architecture execution plan");
        return context;
    }

    internal static bool TryGetActiveCoreVideoContext(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        out WorkflowGenerator generator,
        out VideoExecutionPlanContext context)
    {
        generator = genInfo?.Generator;
        context = null;
        if (generator is null
            || genInfo.ContextID != T2IParamInput.SectionID_Video
            || !DocumentJson.IsActive(generator.UserInput))
        {
            return false;
        }
        context = generator.GetVideoExecutionPlanContext();
        return context is not null;
    }

    private static VideoExecutionPlanContext RequireExistingContext(WorkflowGenerator g) =>
        g.GetVideoExecutionPlanContext()
        ?? throw Invariant.Failure(
            "VideoStages has no executable clips in the active timeline.");

    private static VideoExecutionPlanContext CompilePlan(WorkflowGenerator g)
    {
        TimelineSpec spec = g.GetTimelineSpec();
        if (spec.Clips.Count == 0)
        {
            return null;
        }
        ArchitecturePlanningResult architecturePlanning =
            ArchitecturePlanResolver.Resolve(
                spec,
                VideoArchitectureRegistry.Production,
                g.UserInput.SourceSession);

        bool canInterceptHostCore = RootHostWorkflowFacts.CanInterceptHostCore(g, spec);
        RootEnvironment rootEnvironment = RootEnvironment.FromSpec(spec, canInterceptHostCore);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            rootEnvironment,
            architecturePlanning);
        // Compilation happens once per workflow generator, so this is the one place a plan's
        // non-blocking diagnostics can reach the user without repeating on every phase lookup.
        PlanDiagnosticReporter.ReportToRequest(plan.Diagnostics, g.UserInput);
        return new VideoExecutionPlanContext(
            plan,
            () => new TimelineRunner(g, plan));
    }
}
