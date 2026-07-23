using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;

namespace VideoStages.Architectures.None;

/// <summary>Lazy registration for the architecture-neutral sourced-footage runtime.</summary>
internal sealed class SourceOnlyExecutionAdapter(
    WorkflowGenerator generator) : IArchitectureGenerationSessionFactoryProvider
{
    public ArchitectureId ArchitectureId => NoneArchitecture.Id;

    public IArchitectureGenerationSessionFactory CreateFactory() =>
        new SourceOnlyGenerationSessionFactory(generator);
}

internal sealed class SourceOnlyGenerationSessionFactory(
    WorkflowGenerator generator) : IArchitectureGenerationSessionFactory
{
    public ArchitectureId ArchitectureId => NoneArchitecture.Id;

    public IArchitectureBoundaryAssembler BoundaryAssembler => null;

    public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
    {
    }

    public IVideoGenerationSession CreateSession(ArchitectureTimelineSessionContext context) =>
        new SourceOnlyGenerationSession(generator);

    public void FinalizeTimeline(ArchitectureTimelineFinalizationContext context)
    {
    }
}
