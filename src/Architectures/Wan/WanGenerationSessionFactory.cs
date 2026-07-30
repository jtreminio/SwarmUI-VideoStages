using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo.Runtime;

namespace VideoStages.Architectures.Wan;

internal sealed class WanGenerationSessionFactory(WorkflowGenerator g) :
    IArchitectureGenerationSessionFactory
{
    private HostVideoRootSources _rootSources;

    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;

    /// <summary>Cut-only joins need no architecture-owned merge graph.</summary>
    public IArchitectureBoundaryAssembler BoundaryAssembler => null;

    public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Generated Wan clips start from the image the host left current, whether or not Wan owns
        // the root. Sourced Wan clips ignore this snapshot and install their own clip-local media.
        // On a mixed timeline another architecture may have prepared the host image first, which is
        // harmless because generated Wan clips rescale their own entry image.
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
            new HostVideoStageEngine(g, context.Plan, "Wan"));
    }

    /// <summary>Wan owns no post-assembly graph work.</summary>
    public void FinalizeTimeline(ArchitectureTimelineFinalizationContext context)
    {
    }
}
