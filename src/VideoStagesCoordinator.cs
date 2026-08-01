using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;

namespace VideoStages;

internal sealed class VideoStagesCoordinator(
    WorkflowGenerator g,
    StageSequenceRunner stageSequenceRunner,
    ArchitectureRuntimeSessionFactoryRegistry runtimeFactories)
{
    internal void RunConfiguredStages(VideoExecutionPlanContext planContext)
    {
        ArgumentNullException.ThrowIfNull(planContext);
        planContext.RequirePrepared();
        if (planContext.Plan.Clips.Count == 0)
        {
            return;
        }
        RootRuntimeSession rootSession = RootRuntimeSession.Capture(g, planContext);

        RootExecutionPolicy rootPolicy = new(planContext.Plan);
        AudioRuntimeSources preparedAudioSources = new AudioRuntimeSourceResolver(
            g,
            new AudioHandler(g)).Resolve(planContext.Plan);

        g.LastID = Math.Max(g.LastID, Constants.StagedNodeIdReservationFloor);
        runtimeFactories.PrepareTimeline(new(
            planContext.Plan,
            preparedAudioSources,
            rootPolicy));

        RuntimeArtifact finalArtifact = stageSequenceRunner.Run(
            planContext.Plan,
            preparedAudioSources,
            rootPolicy);
        finalArtifact = new TimelineFrameInterpolator(g).Apply(
            finalArtifact,
            planContext.Plan);
        // Timeline publication metadata is architecture-neutral; VAE ownership stays on the
        // runtime artifact for host saves.
        if (finalArtifact.Media is not null)
        {
            finalArtifact.Media.Compat = null;
            if (finalArtifact.Media.AttachedAudio is not null)
            {
                finalArtifact.Media.AttachedAudio.Compat = null;
            }
        }
        // The common pipeline publishes to the host only after timeline transforms finish.
        finalArtifact.PublishTo(g);
        rootSession.PublishTimeline(finalArtifact);
    }
}
