using SwarmUI.Builtin_ComfyUIBackend;
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
    private readonly IReadOnlyDictionary<
        ArchitectureId,
        IArchitectureGenerationSessionFactoryProvider> _providers;

    internal VideoArchitectureExecutionHost(WorkflowGenerator generator) : this(
        generator,
        VideoArchitectureManifest.CreateProductionRuntimeProviders(generator))
    {
    }

    internal VideoArchitectureExecutionHost(
        WorkflowGenerator generator,
        IEnumerable<IArchitectureGenerationSessionFactoryProvider> providers)
    {
        _generator = generator;
        Dictionary<ArchitectureId, IArchitectureGenerationSessionFactoryProvider> byId = [];
        foreach (IArchitectureGenerationSessionFactoryProvider provider in providers ?? [])
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
    }

    /// <summary>
    /// The single request-preflight owner. It runs before the first VideoStages workflow phase, so
    /// an unsatisfiable request is rejected while the host graph, media and node helpers are still
    /// exactly as the host left them.
    /// </summary>
    internal void PreflightRequest() =>
        PreflightRequest(_generator.RequireVideoExecutionPlanContext().Plan);

    internal void PreflightRequest(VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<PlanDiagnostic> diagnostics = [];
        ArchitectureRequestPreflightContext context = new(plan);
        foreach (IArchitectureGenerationSessionFactoryProvider provider in ActiveProviders(plan))
        {
            diagnostics.AddRange(provider.PreflightRequest(context) ?? []);
        }
        PlanDiagnosticReporter.Report(diagnostics);
        PlanDiagnosticReporter.ThrowIfBlocking(
            diagnostics,
            "VideoStages cannot run this request");
    }

    internal void DispatchHostPhase(ArchitectureHostPhase phase)
    {
        VideoExecutionPlanContext context = _generator.RequireVideoExecutionPlanContext();
        DispatchHostPhase(phase, context.Plan);
    }

    internal void DispatchHostPhase(ArchitectureHostPhase phase, VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (phase == ArchitectureHostPhase.CaptureControlNetPreprocessors)
        {
            new ControlNetCoreMediaCapture(_generator).Capture();
        }
        ArchitectureHostPhaseScope scope = ArchitectureHostPhasePolicy.Scope(phase);
        ArchitectureId? rootOwner = ArchitectureRootOwnerResolver.Resolve(plan);
        IEnumerable<IArchitectureGenerationSessionFactoryProvider> providers =
            scope == ArchitectureHostPhaseScope.RootOwnerOnly
                ? RootOwnerProvider(rootOwner)
                : ActiveProviders(plan);
        foreach (IArchitectureHostPhaseParticipant participant in providers
            .OfType<IArchitectureHostPhaseParticipant>())
        {
            participant.ExecuteHostPhase(new(phase, scope, plan, rootOwner));
        }
    }

    internal IArchitectureRootMediaResizer GetRootMediaResizer()
    {
        VideoExecutionPlan plan = _generator.RequireVideoExecutionPlanContext().Plan;
        return GetRootMediaResizer(plan);
    }

    internal IArchitectureRootMediaResizer GetRootMediaResizer(VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArchitectureId? rootOwner = ArchitectureRootOwnerResolver.Resolve(plan);
        if (rootOwner is null
            || !_providers.TryGetValue(
                rootOwner.Value,
                out IArchitectureGenerationSessionFactoryProvider provider))
        {
            return null;
        }
        return (provider as IArchitectureRootMediaResizerProvider)?.CreateRootMediaResizer();
    }

    internal void RunConfiguredStages() =>
        RunConfiguredStages(_generator.RequireVideoExecutionPlanContext());

    internal void RunConfiguredStages(VideoExecutionPlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Plan.Clips.Count == 0)
        {
            return;
        }
        ArchitectureRuntimeSessionFactoryRegistry runtimeFactories = new(
            ActiveProviders(context.Plan).Select(provider => provider.CreateFactory()));
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

    private IEnumerable<IArchitectureGenerationSessionFactoryProvider> RootOwnerProvider(
        ArchitectureId? rootOwner)
    {
        if (rootOwner is null)
        {
            return [];
        }
        if (!_providers.TryGetValue(
            rootOwner.Value,
            out IArchitectureGenerationSessionFactoryProvider provider))
        {
            throw new InvalidOperationException(
                $"No generation runtime provider is registered for root architecture "
                + $"'{rootOwner.Value}'.");
        }
        return [provider];
    }

    private IReadOnlyList<IArchitectureGenerationSessionFactoryProvider> ActiveProviders(
        VideoExecutionPlan plan)
    {
        List<IArchitectureGenerationSessionFactoryProvider> active = [];
        foreach (ArchitectureId id in plan.Clips
            .Select(clip => clip.Architecture.Id)
            .Distinct())
        {
            if (!_providers.TryGetValue(
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
