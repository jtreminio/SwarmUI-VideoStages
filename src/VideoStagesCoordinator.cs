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
        // Every active execution is plan-backed and owns the host root before any coordinator
        // transform. Unsupported model families fail above.
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
        finalArtifact = new TimelineFrameInterpolator(g).Apply(finalArtifact);
        // Publication metadata describes decoded timeline media, not the model family that
        // happened to produce one clip. VAE ownership remains on RuntimeArtifact for the host save
        // adapter and never enters DecodedClipArtifact or cross-clip assembly.
        if (finalArtifact.Media is not null)
        {
            finalArtifact.Media.Compat = null;
            if (finalArtifact.Media.AttachedAudio is not null)
            {
                finalArtifact.Media.AttachedAudio.Compat = null;
            }
        }
        // This is the common pipeline's only post-clip write to the host compatibility surface.
        finalArtifact.PublishTo(g);
        rootSession.PublishTimeline(finalArtifact);
    }
}
