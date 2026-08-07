using System.Runtime.ExceptionServices;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages;

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
    private readonly Func<TimelineRunner> _createRunner;
    private TimelineRunner _runner;
    private ExceptionDispatchInfo _failure;

    internal VideoExecutionPlanContext(
        VideoExecutionPlan plan,
        Func<TimelineRunner> createRunner)
    {
        ArgumentNullException.ThrowIfNull(createRunner);
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        RootOwnerArchitectureId = ArchitectureRootOwnerResolver.TryResolve(
            plan,
            out ArchitectureId? rootOwner)
            ? rootOwner
            : null;
        _createRunner = createRunner;
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
                throw Invariant.Failure(
                    $"VideoStages request preparation cannot start from state '{State}'.");
            }

            State = VideoExecutionState.Preparing;
            try
            {
                PlanDiagnosticReporter.ThrowIfBlocking(
                    Plan.Diagnostics,
                    "VideoStages could not create a valid architecture execution plan");
                _runner = _createRunner();
                if (!ReferenceEquals(Plan, _runner.Plan))
                {
                    throw Invariant.Failure(
                        "The execution host was created for a different video execution plan.");
                }
                PreflightDiagnostics = _runner.CollectPreflightDiagnostics();
                PlanDiagnosticReporter.ReportToRequest(
                    PreflightDiagnostics,
                    _runner.RequestInput);
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
        throw Invariant.Failure(
            $"VideoStages cannot mutate the workflow while request state is '{State}'. "
                + "Graph-free request preflight must complete first.");
    }

    /// <summary>The runner is private, so this is how a caller reaches a phase on it. Every
    /// workflow phase but the first and last is exactly one of these.</summary>
    internal void ExecutePrepared(Action<TimelineRunner> phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        ExecutePrepared(() => phase(_runner));
    }

    /// <summary>The last phase, which is the only one that also ends the request.</summary>
    internal void RunConfiguredStages()
    {
        ExecutePrepared(host => host.RunConfiguredStages());
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
            throw Invariant.Failure(
                "A completed VideoStages execution cannot transition to failed.", error);
        }
        _failure = ExceptionDispatchInfo.Capture(error);
        State = VideoExecutionState.Failed;
    }
}
