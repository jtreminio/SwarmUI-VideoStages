using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>
/// Production composition root for architecture runtimes. Common workflow entry points dispatch
/// here and do not construct an architecture implementation directly.
/// </summary>
internal sealed class VideoArchitectureExecutionHost
{
    private readonly WorkflowGenerator _generator;
    private readonly VideoExecutionPlan _plan;
    private readonly IReadOnlyDictionary<
        ArchitectureId,
        IArchitectureGenerationSessionFactoryProvider> _providers;
    private readonly IReadOnlyList<IArchitectureGenerationSessionFactoryProvider>
        _activeProviders;
    private readonly ArchitectureId? _rootOwner;
    private VideoExecutionPlanContext _executionContext;

    internal T2IParamInput RequestInput => _generator.UserInput;

    internal VideoArchitectureExecutionHost(
        WorkflowGenerator generator,
        VideoExecutionPlan plan) : this(
        generator,
        plan,
        VideoArchitectureManifest.CreateProductionRuntimeProviders(
            generator,
            ActiveArchitectureIds(plan)))
    {
    }

    internal VideoArchitectureExecutionHost(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        IEnumerable<IArchitectureGenerationSessionFactoryProvider> providers)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ArgumentNullException.ThrowIfNull(providers);
        Dictionary<ArchitectureId, IArchitectureGenerationSessionFactoryProvider> byId = [];
        foreach (IArchitectureGenerationSessionFactoryProvider provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!byId.TryAdd(provider.ArchitectureId, provider))
            {
                throw new InvalidOperationException(
                    $"Duplicate generation runtime provider for architecture "
                    + $"'{provider.ArchitectureId}'.");
            }
        }
        _providers = byId;
        _activeProviders = ResolveActiveProviders(_plan, byId);
        _rootOwner = ArchitectureRootOwnerResolver.Resolve(_plan);
    }

    /// <summary>
    /// The single request-preflight owner. It runs before the first VideoStages workflow phase, so
    /// an unsatisfiable request is rejected while the host graph, media and node helpers are still
    /// exactly as the host left them.
    /// </summary>
    internal IReadOnlyList<PlanDiagnostic> CollectPreflightDiagnostics()
    {
        List<PlanDiagnostic> diagnostics = [
            .. new TimelineFrameInterpolator(_generator).Preflight(_plan)
        ];
        ArchitectureRequestPreflightContext context = new(_plan);
        foreach (IArchitectureGenerationSessionFactoryProvider provider in _activeProviders)
        {
            diagnostics.AddRange(provider.PreflightRequest(context) ?? []);
        }
        return diagnostics.AsReadOnly();
    }

    internal void BindExecutionContext(VideoExecutionPlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.Plan, _plan))
        {
            throw new InvalidOperationException(
                "The execution host cannot bind a different video execution plan.");
        }
        if (_executionContext is not null && !ReferenceEquals(_executionContext, context))
        {
            throw new InvalidOperationException(
                "The execution host is already bound to another VideoStages request.");
        }
        _executionContext = context;
    }

    internal void DispatchHostPhase(ArchitectureHostPhase phase)
    {
        RequireExecutionContext().ExecutePrepared(
            this,
            () => DispatchHostPhaseCore(phase));
    }

    private void DispatchHostPhaseCore(ArchitectureHostPhase phase)
    {
        if (phase == ArchitectureHostPhase.CaptureControlNetPreprocessors)
        {
            new ControlNetCoreMediaCapture(_generator).Capture();
        }
        ArchitectureHostPhaseScope scope = ArchitectureHostPhasePolicy.Scope(phase);
        IEnumerable<IArchitectureGenerationSessionFactoryProvider> providers =
            scope == ArchitectureHostPhaseScope.RootOwnerOnly
                ? RootOwnerProvider()
                : _activeProviders;
        foreach (IArchitectureHostPhaseParticipant participant in providers
            .OfType<IArchitectureHostPhaseParticipant>())
        {
            participant.ExecuteHostPhase(new(phase, scope, _plan, _rootOwner));
        }
    }

    internal IArchitectureRootMediaResizer GetRootMediaResizer()
    {
        return RequireExecutionContext().ExecutePrepared(
            this,
            GetRootMediaResizerCore);
    }

    private IArchitectureRootMediaResizer GetRootMediaResizerCore()
    {
        if (_rootOwner is null
            || !_providers.TryGetValue(
                _rootOwner.Value,
                out IArchitectureGenerationSessionFactoryProvider provider))
        {
            return null;
        }
        return (provider as IArchitectureRootMediaResizerProvider)?.CreateRootMediaResizer();
    }

    internal void RunConfiguredStages()
    {
        VideoExecutionPlanContext context = RequireExecutionContext();
        context.ExecuteToCompletion(this, () => RunConfiguredStagesCore(context));
    }

    private void RunConfiguredStagesCore(VideoExecutionPlanContext context)
    {
        context.RequirePrepared();
        if (_plan.Clips.Count == 0)
        {
            return;
        }
        ArchitectureRuntimeSessionFactoryRegistry runtimeFactories = new(
            _activeProviders.Select(provider => provider.CreateFactory()));
        MultiClipParallelMerger merger = new(
            _generator,
            runtimeFactories.BoundaryAssemblers);
        StageSequenceRunner sequence = new(
            new TimelineAssembler(_generator, merger),
            runtimeFactories);
        new VideoStagesCoordinator(
            _generator,
            sequence,
            runtimeFactories).RunConfiguredStages(context);
    }

    private VideoExecutionPlanContext RequireExecutionContext() =>
        _executionContext
        ?? throw new InvalidOperationException(
            "The execution host is not bound to a prepared VideoStages request.");

    private IEnumerable<IArchitectureGenerationSessionFactoryProvider> RootOwnerProvider()
    {
        if (_rootOwner is null)
        {
            return [];
        }
        if (!_providers.TryGetValue(
            _rootOwner.Value,
            out IArchitectureGenerationSessionFactoryProvider provider))
        {
            throw new InvalidOperationException(
                $"No generation runtime provider is registered for root architecture "
                    + $"'{_rootOwner.Value}'.");
        }
        return [provider];
    }

    private static IReadOnlyList<ArchitectureId> ActiveArchitectureIds(
        VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Array.AsReadOnly(plan.Clips
            .Select(clip => clip.Architecture?.Id
                ?? throw new InvalidOperationException(
                    $"Clip {clip.ClipId} has no architecture identity."))
            .Distinct()
            .ToArray());
    }

    private static IReadOnlyList<IArchitectureGenerationSessionFactoryProvider>
        ResolveActiveProviders(
            VideoExecutionPlan plan,
            IReadOnlyDictionary<
                ArchitectureId,
                IArchitectureGenerationSessionFactoryProvider> providers)
    {
        List<IArchitectureGenerationSessionFactoryProvider> active = [];
        foreach (ArchitectureId id in ActiveArchitectureIds(plan))
        {
            if (!providers.TryGetValue(
                id,
                out IArchitectureGenerationSessionFactoryProvider provider))
            {
                throw new InvalidOperationException(
                    $"No generation runtime provider is registered for architecture '{id}'.");
            }
            active.Add(provider);
        }
        return active;
    }
}
