using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.Architectures.Wan;

internal sealed class WanGenerationSessionFactory(WorkflowGenerator g) :
    IArchitectureGenerationSessionFactory
{
    private WanRootSources _rootSources;

    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;

    /// <summary>Cut-only joins need no architecture-owned merge graph.</summary>
    public IArchitectureBoundaryAssembler BoundaryAssembler => null;

    public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Wan clips all start from the image the host left current, whether or not Wan owns the
        // root. On a mixed timeline another architecture may have prepared that image first, which
        // is harmless here only because Wan rescales its own entry image anyway.
        _rootSources = new(g.CurrentMedia?.Duplicate(), g.CurrentVae?.Duplicate());
    }

    public IVideoGenerationSession CreateSession(ArchitectureTimelineSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_rootSources is null)
        {
            throw new InvalidOperationException(
                "The Wan timeline runtime was not prepared before session creation.");
        }
        return new WanGenerationSession(
            g,
            context.Plan,
            _rootSources,
            new WanStageHostScope(g, context.Plan));
    }

    /// <summary>Wan owns no post-assembly graph work.</summary>
    public void FinalizeTimeline(ArchitectureTimelineFinalizationContext context)
    {
    }
}
