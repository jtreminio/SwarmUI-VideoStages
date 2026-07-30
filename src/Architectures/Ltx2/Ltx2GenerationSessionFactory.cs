using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class Ltx2GenerationSessionFactory(
    WorkflowGenerator generator,
    AudioTimelineExecutor audioTimeline,
    StageSequenceRootSetup rootSetup,
    StageClipExecutor clipExecutor) : IArchitectureGenerationSessionFactory
{
    private StageSequenceRootSources _rootSources;

    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

    public IArchitectureBoundaryAssembler BoundaryAssembler { get; } =
        new Ltx2BoundaryAssembler();

    public ArchitectureTimelineFinalizerScope FinalizerScope =>
        ArchitectureTimelineFinalizerScope.WholeTimelineExclusive;

    public bool HasFinalizationWork(ArchitectureTimelineFinalizationContext context) =>
        context.Plan.Clips.All(clip => clip.Architecture.Id == ArchitectureId)
        && context.Plan.Clips.Any(HdrIcLoraPolicy.IsActive);

    public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
    {
        if (context.OwnsGeneratedRoot)
        {
            audioTimeline.PrepareRootAudio(
                context.Plan,
                context.AudioSources,
                context.RootPolicy);
            _rootSources = rootSetup.Prepare(context.AudioSources, context.RootPolicy);
        }
        else
        {
            _rootSources = rootSetup.Snapshot(context.AudioSources);
        }
    }

    public IVideoGenerationSession CreateSession(ArchitectureTimelineSessionContext context)
    {
        if (_rootSources is null)
        {
            throw new InvalidOperationException(
                "The LTX timeline runtime was not prepared before session creation.");
        }
        return new Ltx2GenerationSession(
            generator,
            clipExecutor,
            _rootSources,
            new StageHostExecutionScope(
                generator,
                context.Plan,
                context.Plan.Clips.Count > 1),
            context.Plan,
            context.Assembly,
            context.RootPolicy);
    }

    public void FinalizeTimeline(ArchitectureTimelineFinalizationContext context)
    {
        if (!HasFinalizationWork(context))
        {
            return;
        }
        new HdrPostprocessApplicator(generator).ApplyHdrPostprocessToFinalSaves(
            context.Plan,
            context.Publication.SaveNodeIds);
    }
}
