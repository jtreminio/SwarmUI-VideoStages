using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Utils;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

internal sealed class VideoStagesCoordinator(
    WorkflowGenerator g,
    StageSequenceRunner stageSequenceRunner,
    AudioTimelineExecutor audioTimelineExecutor)
{
    public void RunConfiguredStages()
    {
        LtxVideoExecutionPlanContext planContext = g.RequireLtxVideoExecutionPlanContext();

        // Every active execution is plan-backed and owns the host root before any coordinator
        // transform. Unsupported model families fail above.
        RootRuntimeSession rootSession = RootRuntimeSession.Capture(g, planContext);

        bool refineSourceVideo = TryInstallRefineSourceVideo(
            planContext.Plan.Clips.Count > 0);
        RootExecutionPolicy rootPolicy = new(
            planContext.Plan.Root,
            RootExecutionFacts.FromPlan(planContext.Plan, refineSourceVideo));
        if (planContext.Plan.Clips.Count > 0)
        {
            EnsureComfyDependencies(planContext.Plan);
        }
        AudioRuntimeSources preparedAudioSources =
            audioTimelineExecutor.PrepareRuntimeSources(planContext.Plan);
        audioTimelineExecutor.PrepareRootAudio(
            planContext.Plan,
            preparedAudioSources,
            rootPolicy);
        if (planContext.Plan.Clips.Count == 0)
        {
            return;
        }

        g.LastID = Math.Max(g.LastID, Constants.StagedNodeIdReservationFloor);

        stageSequenceRunner.Run(
            planContext.Plan,
            preparedAudioSources,
            rootPolicy);
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        RuntimeArtifact finalArtifact = RuntimeArtifact.Capture(
            g,
            bridge,
            ArtifactOrigin.ClipAssembly);
        OutputPublication publication = rootSession.PublishTimeline(finalArtifact);
        new HdrPostprocessApplicator(g).ApplyHdrPostprocessToFinalSaves(
            planContext.Plan,
            publication.SaveNodeIds);
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

    private void EnsureComfyDependencies(VideoExecutionPlan plan)
    {
        if (g.Features.Contains(Constants.LtxVideoFeatureFlag)
            || !plan.Clips.Any(clip => clip.Stages.Any(stage => !stage.IcLoras.IsDefaultOrEmpty)))
        {
            return;
        }

        throw new SwarmUserErrorException(
            "VideoStages IC-LoRAs require the ComfyUI-LTXVideo custom nodes. "
            + $"Install {Constants.LtxVideoNodeUrl} or use SwarmUI's LTXVideo feature installer.");
    }

}
