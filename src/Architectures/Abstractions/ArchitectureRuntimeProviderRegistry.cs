namespace VideoStages.Architectures.Abstractions;

/// <summary>Coordinates the active runtime providers for one timeline.</summary>
internal sealed class ArchitectureRuntimeProviderRegistry
{
    private readonly IReadOnlyList<IArchitectureGenerationSessionProvider> _activeProviders;
    private readonly ArchitectureId? _rootOwner;

    internal ArchitectureRuntimeProviderRegistry(
        IEnumerable<IArchitectureGenerationSessionProvider> providers,
        VideoExecutionPlanContext request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _activeProviders = Array.AsReadOnly((providers ?? []).ToArray());
        _rootOwner = request.RootOwnerArchitectureId;
    }

    internal ArchitectureRuntimeDispatcher CreateDispatcher(
        ArchitectureTimelineSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context?.Plan);
        return new ArchitectureRuntimeDispatcher(
            _activeProviders.Select(provider => provider.CreateSession(context with
            {
                OwnsGeneratedRoot = _rootOwner == provider.ArchitectureId,
            })));
    }

    internal IReadOnlyDictionary<ArchitectureId, IArchitectureBoundaryAssembler>
        BoundaryAssemblers =>
        _activeProviders
            .Select(provider => (provider.ArchitectureId, provider.BoundaryAssembler))
            .Where(pair => pair.BoundaryAssembler is not null)
            .ToDictionary(pair => pair.ArchitectureId, pair => pair.BoundaryAssembler);
}
