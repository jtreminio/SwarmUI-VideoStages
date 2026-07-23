using System.Runtime.CompilerServices;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Planning;

namespace VideoStages;

internal static class VideoStagesContext
{
    private static readonly ConditionalWeakTable<WorkflowGenerator, VideoStagesSpec> Cache = new();

    // The execution plan is deliberately a separate cache from the parsed specification. The
    // specification remains available to every historical path (including WAN); this cache is
    // the canonical LTX-only execution seam.
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
    /// selected models make it an LTX timeline. Its parity diagnostics preserve the root-handoff
    /// characterization that guided the migration, while runtime orchestration consumes the plan.
    /// </summary>
    public static LtxVideoExecutionPlanContext? GetLtxVideoExecutionPlanContext(this WorkflowGenerator g) =>
        LtxPlanCache.GetValue(g, CompileLtxPlan).Context;

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

        bool legacyRootInterception = RootVideoStageHandoff.ShouldHandoffRootStageLegacy(g, spec);
        RootEnvironment rootEnvironment = new(
            spec.IsTextToVideo ? HostRootKind.TextToVideoRoot : HostRootKind.ImageToVideo,
            CanHandoffHostCore: legacyRootInterception,
            HasGlobalRefineSource: HasVideoRefineSource(g));
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(spec, rootEnvironment);
        bool planRequestsRootInterception = plan.Root.CoreDisposition is not HostCoreDisposition.Keep;
        List<VideoExecutionPlanParityDiagnostic> parityDiagnostics = [];
        if (legacyRootInterception != planRequestsRootInterception)
        {
            parityDiagnostics.Add(new(
                "root-interception-mismatch",
                "The immutable root plan and the legacy root interception predicate disagree. "
                    + "The comparison is retained as a migration characterization diagnostic.",
                legacyRootInterception,
                planRequestsRootInterception));
        }
        return new LtxPlanCacheEntry(new LtxVideoExecutionPlanContext(
            plan,
            legacyRootInterception,
            planRequestsRootInterception,
            parityDiagnostics.AsReadOnly()));
    }

    private static bool IsLtxTimeline(WorkflowGenerator g, VideoStagesSpec spec)
    {
        // A mixed timeline remains on the legacy path: the canonical executor has one
        // model-family contract, rather than silently treating an LTX stage as permission to
        // plan adjacent WAN/unknown stages as LTX.
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
/// before the native image-to-video step runs. The context records parity with the historical root
/// predicate as a migration guard while current LTX orchestration consumes the plan.
/// </summary>
internal sealed record LtxVideoExecutionPlanContext(
    VideoExecutionPlan Plan,
    bool LegacyRootInterception,
    bool PlanRequestsRootInterception,
    IReadOnlyList<VideoExecutionPlanParityDiagnostic> ParityDiagnostics)
{
    public bool HasRootInterceptionParity => ParityDiagnostics.Count == 0;
}

internal sealed record VideoExecutionPlanParityDiagnostic(
    string Code,
    string Message,
    bool LegacyValue,
    bool PlanValue);
