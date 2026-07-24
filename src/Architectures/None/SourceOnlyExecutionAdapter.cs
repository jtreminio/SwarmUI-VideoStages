using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;

namespace VideoStages.Architectures.None;

/// <summary>Lazy registration for the architecture-neutral sourced-footage runtime.</summary>
internal sealed class SourceOnlyExecutionAdapter(
    WorkflowGenerator generator) :
    IArchitectureGenerationSessionFactoryProvider,
    IArchitectureHostPhaseParticipant
{
    public ArchitectureId ArchitectureId => NoneArchitecture.Id;

    /// <summary>
    /// A sourced-only clip can still select ControlNet audio, and
    /// <see cref="AudioRuntimeSourceResolver"/> resolves that from the core ControlNet capture. The
    /// capture is architecture-neutral, so this adapter performs it too — otherwise a timeline with
    /// no generation stages plans successfully and then throws a user error mid-execution.
    /// Every other host phase is root-owner scoped and belongs to a generating architecture.
    /// </summary>
    public void ExecuteHostPhase(ArchitectureHostPhaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Phase == ArchitectureHostPhase.CaptureControlNetPreprocessors)
        {
            new ControlNetCoreMediaCapture(generator).Capture();
        }
    }

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
