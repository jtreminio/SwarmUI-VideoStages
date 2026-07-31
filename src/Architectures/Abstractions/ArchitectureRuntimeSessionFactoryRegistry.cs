using VideoStages.Planning;

namespace VideoStages.Architectures.Abstractions;

/// <summary>
/// Resolves active architecture factories and coordinates their timeline lifecycle.
/// </summary>
internal sealed class ArchitectureRuntimeSessionFactoryRegistry
{
    private readonly VideoExecutionPlan _plan;
    private readonly IReadOnlyList<IArchitectureGenerationSessionFactory> _activeFactories;
    private readonly ArchitectureId? _rootOwner;

    internal ArchitectureRuntimeSessionFactoryRegistry(
        IEnumerable<IArchitectureGenerationSessionFactory> factories,
        VideoExecutionPlanContext request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _plan = request.Plan;
        Dictionary<ArchitectureId, IArchitectureGenerationSessionFactory> byId = [];
        foreach (IArchitectureGenerationSessionFactory factory in factories ?? [])
        {
            ArgumentNullException.ThrowIfNull(factory);
            if (!byId.TryAdd(factory.ArchitectureId, factory))
            {
                throw new InvalidOperationException(
                    $"Duplicate generation session factory for architecture "
                    + $"'{factory.ArchitectureId}'.");
            }
        }
        List<IArchitectureGenerationSessionFactory> active = [];
        foreach (ArchitectureId id in request.ActiveArchitectureIds)
        {
            if (!byId.TryGetValue(id, out IArchitectureGenerationSessionFactory factory))
            {
                throw new InvalidOperationException(
                    $"No generation session factory is registered for architecture '{id}'.");
            }
            active.Add(factory);
        }
        _activeFactories = active.AsReadOnly();
        _rootOwner = request.RootOwnerArchitectureId;
    }

    internal void PrepareTimeline(ArchitectureTimelinePreparationContext context)
    {
        RequirePlan(context?.Plan);
        foreach (IArchitectureGenerationSessionFactory factory in _activeFactories)
        {
            factory.PrepareTimeline(context with
            {
                OwnsGeneratedRoot = _rootOwner == factory.ArchitectureId,
            });
        }
    }

    internal ArchitectureRuntimeDispatcher CreateDispatcher(
        ArchitectureTimelineSessionContext context)
    {
        RequirePlan(context?.Plan);
        return new ArchitectureRuntimeDispatcher(
            _activeFactories.Select(factory => factory.CreateSession(context)));
    }

    internal void FinalizeTimeline(ArchitectureTimelineFinalizationContext context)
    {
        RequirePlan(context?.Plan);
        IArchitectureGenerationSessionFactory[] finalizers = [
            .. _activeFactories.Where(factory =>
                factory.HasFinalizationWork(context))
        ];
        if (finalizers.Length > 1)
        {
            throw new InvalidOperationException(
                "Multiple architectures attempted to own whole-timeline finalization: "
                + string.Join(", ", finalizers.Select(factory => $"'{factory.ArchitectureId}'")));
        }
        if (finalizers.SingleOrDefault() is { } finalizer)
        {
            finalizer.FinalizeTimeline(context);
        }
    }

    internal IReadOnlyDictionary<ArchitectureId, IArchitectureBoundaryAssembler>
        BoundaryAssemblers =>
        _activeFactories
            .Where(factory => factory.BoundaryAssembler is not null)
            .ToDictionary(factory => factory.ArchitectureId, factory => factory.BoundaryAssembler);

    private void RequirePlan(VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ReferenceEquals(plan, _plan))
        {
            throw new InvalidOperationException(
                "The architecture runtime registry cannot execute a different video plan.");
        }
    }

}
