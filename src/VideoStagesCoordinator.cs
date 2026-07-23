using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;

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
        runtimeFactories.PreflightTimeline(new(planContext.Plan));

        // Every active execution is plan-backed and owns the host root before any coordinator
        // transform. Unsupported model families fail above.
        RootRuntimeSession rootSession = RootRuntimeSession.Capture(g, planContext);

        bool refineSourceVideo = TryInstallRefineSourceVideo(
            planContext.Plan.Clips.Count > 0);
        RootExecutionPolicy rootPolicy = new(
            planContext.Plan.Root,
            RootExecutionFacts.FromPlan(planContext.Plan, refineSourceVideo));
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

    private bool TryInstallRefineSourceVideo(bool hasClips)
    {
        if (!g.UserInput.TryGet(VideoStagesExtension.RefineSourceVideo, out Image refineSource)
            || refineSource is null
            || !hasClips)
        {
            return false;
        }
        if (refineSource.Type?.MetaType != MediaMetaType.Video)
        {
            Logs.Warning(
                "VideoStages: 'Refine Source Video' was set but its media type is not video. "
                + "Ignoring and falling back to the normal pipeline.");
            return false;
        }

        WGNodeData loadedVideo = g.LoadImage(refineSource, "${vsrefinesource}", resize: false);
        g.CurrentMedia = loadedVideo;
        return true;
    }

}
