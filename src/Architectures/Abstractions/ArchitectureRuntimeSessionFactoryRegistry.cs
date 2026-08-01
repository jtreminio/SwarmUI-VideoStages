using VideoStages.Planning;

namespace VideoStages.Architectures.Abstractions;

/// <summary>
/// Resolves active architecture factories and coordinates their timeline lifecycle.
/// </summary>
internal sealed class ArchitectureRuntimeSessionFactoryRegistry
{
    private readonly IReadOnlyList<IArchitectureGenerationSessionFactory> _activeFactories;
    private readonly ArchitectureId? _rootOwner;

    internal ArchitectureRuntimeSessionFactoryRegistry(
        IEnumerable<IArchitectureGenerationSessionFactory> factories,
        VideoExecutionPlanContext request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _activeFactories = Array.AsReadOnly((factories ?? []).ToArray());
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

    internal IReadOnlyDictionary<ArchitectureId, IArchitectureBoundaryAssembler>
        BoundaryAssemblers =>
        _activeFactories
            .Select(factory => (factory.ArchitectureId, factory.BoundaryAssembler))
            .Where(pair => pair.BoundaryAssembler is not null)
            .ToDictionary(pair => pair.ArchitectureId, pair => pair.BoundaryAssembler);

    private static void RequirePlan(VideoExecutionPlan plan) =>
        ArgumentNullException.ThrowIfNull(plan);

}
