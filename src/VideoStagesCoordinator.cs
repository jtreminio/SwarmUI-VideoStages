using ComfyTyped.Core;
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
    public void RunConfiguredStages() =>
        RunConfiguredStages(g.RequireVideoExecutionPlanContext());

    internal void RunConfiguredStages(VideoExecutionPlanContext planContext)
    {
        ArgumentNullException.ThrowIfNull(planContext);
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

        stageSequenceRunner.Run(
            planContext.Plan,
            preparedAudioSources,
            rootPolicy);
        new TimelineFrameInterpolator(g).Apply();
        // Publication metadata describes decoded timeline media, not the model family that
        // happened to produce one clip. VAE ownership remains on RuntimeArtifact for the host save
        // adapter and never enters DecodedClipArtifact or cross-clip assembly.
        if (g.CurrentMedia is not null)
        {
            g.CurrentMedia.Compat = null;
            if (g.CurrentMedia.AttachedAudio is not null)
            {
                g.CurrentMedia.AttachedAudio.Compat = null;
            }
        }
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        RuntimeArtifact finalArtifact = RuntimeArtifact.Capture(
            g,
            bridge,
            ArtifactOrigin.ClipAssembly);
        OutputPublication publication = rootSession.PublishTimeline(finalArtifact);
        runtimeFactories.FinalizeTimeline(new(
            planContext.Plan,
            publication));
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
