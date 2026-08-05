using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages;

internal static class VideoStagesContext
{
    private static readonly ConditionalWeakTable<WorkflowGenerator, VideoStagesSpec> Cache = new();

    // The execution plan is deliberately a separate cache from the parsed specification so it
    // can be compiled before the host graph exists and consumed by every workflow phase.
    private static readonly ConditionalWeakTable<WorkflowGenerator, VideoExecutionPlanContext> PlanCache = new();

    // Prompt-tag section resolution has no live WorkflowGenerator to key the cache on, so repeated
    // videoclip[clip,stage] lookups in one generation share the parse via the stable param input.
    private static readonly ConditionalWeakTable<T2IParamInput, VideoStagesSpec> PromptParseCache = new();

    public static VideoStagesSpec GetVideoStagesSpec(this WorkflowGenerator g) =>
        Cache.GetValue(g, RequestReader.Read);

    public static VideoStagesSpec GetVideoStagesSpecForPromptParse(T2IParamInput input) =>
        PromptParseCache.GetValue(input, ParseForPromptTag);

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
            || !DocumentJson.IsActive(generator))
        {
            return false;
        }
        context = generator.GetVideoExecutionPlanContext();
        return context is not null;
    }

    private static VideoExecutionPlanContext RequireExistingContext(WorkflowGenerator g) =>
        g.GetVideoExecutionPlanContext()
        ?? throw VideoStagesInvariant.Failure(
            "VideoStages has no executable clips in the active timeline.");

    private static VideoStagesSpec ParseForPromptTag(T2IParamInput input)
    {
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/"
        };
        return RequestReader.Read(generator);
    }

    private static VideoExecutionPlanContext CompilePlan(WorkflowGenerator g)
    {
        VideoStagesSpec spec = g.GetVideoStagesSpec();
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
        RootEnvironment rootEnvironment = new(
            spec.IsTextToVideo ? HostRootKind.TextToVideoRoot : HostRootKind.ImageToVideo,
            CanHandoffHostCore: canInterceptHostCore);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            spec,
            rootEnvironment,
            architecturePlanning);
        // Compilation happens once per workflow generator, so this is the one place a plan's
        // non-blocking diagnostics can reach the user without repeating on every phase lookup.
        PlanDiagnosticReporter.ReportToRequest(plan.Diagnostics, g.UserInput);
        return new VideoExecutionPlanContext(
            plan,
            () => new VideoArchitectureExecutionHost(g, plan));
    }
}

internal enum VideoExecutionState
{
    Compiled,
    Preparing,
    Prepared,
    Failed,
    Completed,
}

/// <summary>Request-scoped plan state and runtime binding.</summary>
internal sealed class VideoExecutionPlanContext
{
    private readonly object _preparationLock = new();
    private readonly Func<VideoArchitectureExecutionHost> _createExecutionHost;
    private VideoArchitectureExecutionHost _executionHost;
    private ExceptionDispatchInfo _failure;

    internal VideoExecutionPlanContext(VideoExecutionPlan plan) : this(plan, null)
    {
    }

    internal VideoExecutionPlanContext(
        VideoExecutionPlan plan,
        Func<VideoArchitectureExecutionHost> createExecutionHost)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        RootOwnerArchitectureId = ArchitectureRootOwnerResolver.TryResolve(
            plan,
            out ArchitectureId? rootOwner)
            ? rootOwner
            : null;
        _createExecutionHost = createExecutionHost;
    }

    public VideoExecutionPlan Plan { get; }
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
                throw VideoStagesInvariant.Failure(
                    $"VideoStages request preparation cannot start from state '{State}'.");
            }

            State = VideoExecutionState.Preparing;
            try
            {
                PlanDiagnosticReporter.ThrowIfBlocking(
                    Plan.Diagnostics,
                    "VideoStages could not create a valid architecture execution plan");
                _executionHost = _createExecutionHost?.Invoke()
                    ?? throw VideoStagesInvariant.Failure(
                        "This video execution plan context has no runtime provider binding.");
                if (!ReferenceEquals(Plan, _executionHost.Plan))
                {
                    throw VideoStagesInvariant.Failure(
                        "The execution host was created for a different video execution plan.");
                }
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
        // Do not prepare lazily from a mutation callback: alternate host callbacks run after core
        // graph construction. The registered graph-free preflight phase is the sole preparation
        // owner, and skipping it must fail before this extension mutates anything.
        throw VideoStagesInvariant.Failure(
            $"VideoStages cannot mutate the workflow while request state is '{State}'. "
                + "Graph-free request preflight must complete first.");
    }

    internal void CaptureControlNetPreprocessors() => ExecutePrepared(() =>
        _executionHost.CaptureControlNetPreprocessors());

    internal void CaptureBaseReference() => ExecutePrepared(() =>
        _executionHost.CaptureBaseReference());

    internal void CaptureRefinerReference() => ExecutePrepared(() =>
        _executionHost.CaptureRefinerReference());

    internal void CapturePreCoreMedia() => ExecutePrepared(() =>
        _executionHost.CapturePreCoreMedia());

    internal void DropCoreOutput() => ExecutePrepared(() =>
        _executionHost.DropCoreOutput());

    internal void ApplyRootAudioMaskDimensions() => ExecutePrepared(() =>
        _executionHost.ApplyRootAudioMaskDimensions());

    internal void RunConfiguredStages()
    {
        ExecutePrepared(() => _executionHost.RunConfiguredStages());
        State = VideoExecutionState.Completed;
    }

    internal void ExecutePrepared(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RequirePrepared();
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

    private void FailExecution(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (State == VideoExecutionState.Completed)
        {
            throw VideoStagesInvariant.Failure(
                "A completed VideoStages execution cannot transition to failed.", error);
        }
        _failure = ExceptionDispatchInfo.Capture(error);
        State = VideoExecutionState.Failed;
    }
}
