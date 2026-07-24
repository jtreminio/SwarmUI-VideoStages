using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class Ltx2GenerationSessionFactory(
    WorkflowGenerator generator,
    AudioTimelineExecutor audioTimeline,
    StageSequenceRootSetup rootSetup,
    StageGuideReferenceState guideReferences,
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

    public void PreflightTimeline(ArchitectureTimelinePreflightContext context)
    {
        if (!Ltx2HostIntegration.IsAvailable(generator.Features)
            && context.Plan.Clips
                .Where(clip => clip.Architecture.Id == ArchitectureId)
                .Any(clip => clip.Stages.Any(stage =>
                    !stage.RequireLtx2Payload().IcLoras.IsDefaultOrEmpty)))
        {
            throw new SwarmUserErrorException(
                "VideoStages IC-LoRAs require the ComfyUI-LTXVideo custom nodes. "
                + $"Install {Ltx2HostIntegration.NodeUrl} "
                + "or use SwarmUI's LTXVideo feature installer.");
        }
    }

    public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
    {
        guideReferences.BeginClip();
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
                context.Plan.Clips.Count > 1));
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
