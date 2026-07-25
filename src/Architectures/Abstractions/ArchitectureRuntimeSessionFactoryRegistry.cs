using VideoStages.Planning;

namespace VideoStages.Architectures.Abstractions;

/// <summary>
/// Resolves active architecture factories and coordinates their timeline lifecycle.
/// </summary>
internal sealed class ArchitectureRuntimeSessionFactoryRegistry
{
    private readonly IReadOnlyDictionary<ArchitectureId, IArchitectureGenerationSessionFactory>
        _factories;

    internal ArchitectureRuntimeSessionFactoryRegistry(
        IEnumerable<IArchitectureGenerationSessionFactory> factories)
    {
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
        _factories = byId;
    }

    internal void PrepareTimeline(ArchitectureTimelinePreparationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArchitectureId? rootOwner = ArchitectureRootOwnerResolver.Resolve(context.Plan);
        foreach (IArchitectureGenerationSessionFactory factory in ActiveFactories(context.Plan))
        {
            factory.PrepareTimeline(context with
            {
                OwnsGeneratedRoot = rootOwner == factory.ArchitectureId,
            });
        }
    }

    internal ArchitectureRuntimeDispatcher CreateDispatcher(
        ArchitectureTimelineSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ArchitectureRuntimeDispatcher(
            ActiveFactories(context.Plan).Select(factory => factory.CreateSession(context)));
    }

    internal void FinalizeTimeline(ArchitectureTimelineFinalizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<IArchitectureGenerationSessionFactory> active =
            ActiveFactories(context.Plan);
        IArchitectureGenerationSessionFactory[] exclusive = [
            .. active.Where(factory =>
                factory.FinalizerScope == ArchitectureTimelineFinalizerScope.WholeTimelineExclusive
                && factory.HasFinalizationWork(context))
        ];
        if (exclusive.Length > 1)
        {
            throw new InvalidOperationException(
                "Multiple architectures attempted to own whole-timeline finalization: "
                + string.Join(", ", exclusive.Select(factory => $"'{factory.ArchitectureId}'")));
        }
        foreach (IArchitectureGenerationSessionFactory factory in active.Where(
            factory => factory.HasFinalizationWork(context)))
        {
            factory.FinalizeTimeline(context);
        }
    }

    internal IReadOnlyDictionary<ArchitectureId, IArchitectureBoundaryAssembler>
        BoundaryAssemblers =>
        _factories.Values
            .Where(factory => factory.BoundaryAssembler is not null)
            .ToDictionary(factory => factory.ArchitectureId, factory => factory.BoundaryAssembler);

    private IReadOnlyList<IArchitectureGenerationSessionFactory> ActiveFactories(
        VideoExecutionPlan plan)
    {
        List<IArchitectureGenerationSessionFactory> active = [];
        foreach (ArchitectureId id in plan.Clips
            .Select(clip => clip.Architecture.Id)
            .Distinct())
        {
            if (!_factories.TryGetValue(id, out IArchitectureGenerationSessionFactory factory))
            {
                throw new InvalidOperationException(
                    $"No generation session factory is registered for architecture '{id}'.");
            }
            active.Add(factory);
        }
        return active;
    }
}
