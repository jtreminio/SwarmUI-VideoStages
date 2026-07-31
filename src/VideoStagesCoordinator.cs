using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

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

        InstallRefineSourceVideo(planContext.Plan.Root);
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

    private void InstallRefineSourceVideo(RootPlan root)
    {
        bool hasVideoRefineSource =
            g.UserInput.TryGet(VideoStagesExtension.RefineSourceVideo, out Image refineSource)
            && refineSource is not null
            && refineSource.Type?.MetaType == MediaMetaType.Video;
        if (!RefineSourceInstallPolicy.RequiresInstall(root, hasVideoRefineSource))
        {
            return;
        }
        g.CurrentMedia = g.LoadImage(refineSource, "${vsrefinesource}", resize: false);
    }
}

/// <summary>
/// The plan already decided whether this timeline is a global refine, so runtime installation
/// either succeeds or fails the generation. It cannot quietly fall back to the normal pipeline
/// against a root plan already committed to refine semantics.
/// </summary>
internal static class RefineSourceInstallPolicy
{
    internal static bool RequiresInstall(RootPlan root, bool hasVideoRefineSource)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.HostKind != HostRootKind.GlobalRefineSource)
        {
            return false;
        }
        return hasVideoRefineSource
            ? true
            : throw new SwarmUserErrorException(
                "VideoStages: this timeline was planned as a global refine of 'Refine Source "
                + "Video', but that parameter no longer holds a video.");
    }
}
