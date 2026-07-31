using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
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
            || !VideoStagesPromptSection.IsActive(generator))
        {
            return false;
        }
        context = generator.GetVideoExecutionPlanContext();
        return context is not null;
    }

    private static VideoExecutionPlanContext RequireExistingContext(WorkflowGenerator g) =>
        g.GetVideoExecutionPlanContext()
        ?? throw new SwarmUserErrorException(
            "VideoStages has no executable clips in the active timeline.");

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
            ArchitecturePlanResolver.Resolve(
                spec,
                VideoArchitectureRegistry.Production,
                g.UserInput.SourceSession);

        bool canInterceptHostCore = RootHostWorkflowFacts.CanInterceptHostCore(g, spec);
        RootEnvironment rootEnvironment = new(
            spec.IsTextToVideo ? HostRootKind.TextToVideoRoot : HostRootKind.ImageToVideo,
            CanHandoffHostCore: canInterceptHostCore,
            HasGlobalRefineSource: HasVideoRefineSource(g));
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            rootEnvironment,
            architecturePlanning);
        // Compilation happens once per workflow generator, so this is the one place a plan's
        // non-blocking diagnostics can reach the user without repeating on every phase lookup.
        PlanDiagnosticReporter.ReportToRequest(plan.Diagnostics, g.UserInput);
        return new PlanCacheEntry(new VideoExecutionPlanContext(
            plan,
            context => new VideoArchitectureExecutionHost(g, context)));
    }

    private static bool HasVideoRefineSource(WorkflowGenerator g)
    {
        if (!g.UserInput.TryGet(VideoStagesExtension.RefineSourceVideo, out Image source)
            || source is null)
        {
            return false;
        }
        if (source.Type?.MetaType == SwarmUI.Media.MediaMetaType.Video)
        {
            return true;
        }
        // Planning declines global-refine semantics here, so this is the only point at which the
        // user can be told why their configured refine source did nothing.
        PlanDiagnosticReporter.TrackRequestWarning(
            g.UserInput,
            "VideoStages: 'Refine Source Video' was set but its media type is not video. "
            + "Ignoring and falling back to the normal pipeline.");
        return false;
    }

    private sealed record PlanCacheEntry(VideoExecutionPlanContext? Context);
}

internal enum VideoExecutionState
{
    Compiled,
    Preparing,
    Prepared,
    Failed,
    Completed,
}

/// <summary>
/// One request-scoped execution contract. The immutable plan remains inspectable before and after
/// execution; provider binding and graph-free preflight happen once before any mutating phase.
/// Mutable timeline factories and sessions are deliberately created only by the final run.
/// </summary>
internal sealed class VideoExecutionPlanContext
{
    private readonly object _preparationLock = new();
    private readonly Func<VideoExecutionPlanContext, VideoArchitectureExecutionHost>
        _createExecutionHost;
    private VideoArchitectureExecutionHost _executionHost;
    private ExceptionDispatchInfo _failure;

    internal VideoExecutionPlanContext(VideoExecutionPlan plan) : this(plan, null)
    {
    }

    internal VideoExecutionPlanContext(
        VideoExecutionPlan plan,
        Func<VideoExecutionPlanContext, VideoArchitectureExecutionHost> createExecutionHost)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ActiveArchitectureIds = Array.AsReadOnly(plan.Clips
            .Where(clip => clip.Architecture is not null)
            .Select(clip => clip.Architecture.Id)
            .Distinct()
            .ToArray());
        RootOwnerArchitectureId = ArchitectureRootOwnerResolver.TryResolve(
            plan,
            out ArchitectureId? rootOwner)
            ? rootOwner
            : null;
        _createExecutionHost = createExecutionHost;
    }

    public VideoExecutionPlan Plan { get; }
    public IReadOnlyList<ArchitectureId> ActiveArchitectureIds { get; }
    public ArchitectureId? RootOwnerArchitectureId { get; }

    public VideoExecutionState State { get; private set; } =
        VideoExecutionState.Compiled;

    public IReadOnlyList<PlanDiagnostic> PreflightDiagnostics { get; private set; } = [];

    internal void PrepareRequest()
    {
        lock (_preparationLock)
        {
            if (State == VideoExecutionState.Prepared)
            {
                return;
            }
            if (State == VideoExecutionState.Failed)
            {
                _failure.Throw();
            }
            if (State != VideoExecutionState.Compiled)
            {
                throw new InvalidOperationException(
                    $"VideoStages request preparation cannot start from state '{State}'.");
            }

            State = VideoExecutionState.Preparing;
            try
            {
                PlanDiagnosticReporter.ThrowIfBlocking(
                    Plan.Diagnostics,
                    "VideoStages could not create a valid architecture execution plan");
                _executionHost = _createExecutionHost?.Invoke(this)
                    ?? throw new InvalidOperationException(
                        "This video execution plan context has no runtime provider binding.");
                _executionHost.BindExecutionContext(this);
                PreflightDiagnostics = _executionHost.CollectPreflightDiagnostics();
                PlanDiagnosticReporter.ReportToRequest(
                    PreflightDiagnostics,
                    _executionHost.RequestInput);
                PlanDiagnosticReporter.ThrowIfBlocking(
                    PreflightDiagnostics,
                    "VideoStages cannot run this request");
                State = VideoExecutionState.Prepared;
            }
            catch (Exception error)
            {
                _failure = ExceptionDispatchInfo.Capture(error);
                State = VideoExecutionState.Failed;
                throw;
            }
        }
    }

    internal VideoArchitectureExecutionHost RequirePreparedExecutionHost()
    {
        RequirePrepared();
        return _executionHost;
    }

    internal void RequirePrepared()
    {
        if (State == VideoExecutionState.Prepared)
        {
            return;
        }
        if (State == VideoExecutionState.Failed)
        {
            _failure.Throw();
        }
        PlanDiagnosticReporter.ThrowIfBlocking(
            Plan.Diagnostics,
            "VideoStages could not create a valid architecture execution plan");
        // Do not prepare lazily from a mutation callback: alternate host callbacks run after core
        // graph construction. The registered graph-free preflight phase is the sole preparation
        // owner, and skipping it must fail before this extension mutates anything.
        throw new InvalidOperationException(
            $"VideoStages cannot mutate the workflow while request state is '{State}'. "
                + "Graph-free request preflight must complete first.");
    }

    internal void ExecutePrepared(
        VideoArchitectureExecutionHost executionHost,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RequireBoundPreparedHost(executionHost);
        try
        {
            action();
        }
        catch (Exception error)
        {
            FailExecution(error);
            throw;
        }
    }

    internal void ExecutePrepared(Action action) =>
        ExecutePrepared(RequirePreparedExecutionHost(), action);

    internal T ExecutePrepared<T>(
        VideoArchitectureExecutionHost executionHost,
        Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RequireBoundPreparedHost(executionHost);
        try
        {
            return action();
        }
        catch (Exception error)
        {
            FailExecution(error);
            throw;
        }
    }

    internal void ExecuteToCompletion(
        VideoArchitectureExecutionHost executionHost,
        Action action)
    {
        ExecutePrepared(executionHost, action);
        State = VideoExecutionState.Completed;
    }

    private void RequireBoundPreparedHost(
        VideoArchitectureExecutionHost executionHost)
    {
        ArgumentNullException.ThrowIfNull(executionHost);
        if (!ReferenceEquals(RequirePreparedExecutionHost(), executionHost))
        {
            throw new InvalidOperationException(
                "This execution host is not bound to the prepared VideoStages request.");
        }
    }

    private void FailExecution(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (State == VideoExecutionState.Completed)
        {
            throw new InvalidOperationException(
                "A completed VideoStages execution cannot transition to failed.", error);
        }
        _failure = ExceptionDispatchInfo.Capture(error);
        State = VideoExecutionState.Failed;
    }
}
