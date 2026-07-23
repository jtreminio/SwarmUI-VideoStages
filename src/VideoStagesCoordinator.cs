using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Utils;
using VideoStages.Execution;

namespace VideoStages;

internal sealed class VideoStagesCoordinator(
    WorkflowGenerator g,
    RootVideoStageHandoff rootVideoStageHandoff,
    StageSequenceRunner stageSequenceRunner,
    AudioTimelineExecutor audioTimelineExecutor)
{
    public void RunConfiguredStages()
    {
        VideoStagesSpec spec = g.GetVideoStagesSpec();
        LtxVideoExecutionPlanContext planContext = g.RequireLtxVideoExecutionPlanContext();

        // Every active execution is plan-backed and owns the host root before any coordinator
        // transform. Unsupported model families fail above.
        RootRuntimeSession rootSession = RootRuntimeSession.Capture(g, planContext);

        List<ClipSpec> clips = [.. spec.Clips];
        bool refineSourceVideo = TryInstallRefineSourceVideo(clips);
        // A sourced first clip contributes footage, not a generation, so it can't absorb the core
        // root stage; keep the root alive as the guide/source for the generated clips (mirroring
        // the refine-source rule).
        bool firstClipSourced = clips.Count > 0 && clips[0].SourceVideo is not null;
        bool rootStageHandoff = !refineSourceVideo
            && !firstClipSourced
            && rootVideoStageHandoff.ShouldHandoffRootStage();
        if (clips.Count > 0)
        {
            EnsureComfyDependencies(clips);
        }
        PreparedAudioRuntimeSources preparedAudioSources =
            audioTimelineExecutor.PrepareRuntimeSources(clips, planContext.Plan);
        audioTimelineExecutor.PrepareRootAudio(
            clips,
            planContext.Plan,
            preparedAudioSources,
            rootStageHandoff,
            firstClipSourced);
        if (clips.Count == 0)
        {
            return;
        }

        g.LastID = Math.Max(g.LastID, Constants.StagedNodeIdReservationFloor);

        stageSequenceRunner.Run(
            clips,
            planContext.Plan,
            preparedAudioSources,
            rootStageHandoff: rootStageHandoff);
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        RuntimeArtifact finalArtifact = RuntimeArtifact.Capture(
            g,
            bridge,
            ArtifactOrigin.ClipAssembly);
        OutputPublication publication = rootSession.PublishTimeline(finalArtifact);
        new HdrPostprocessApplicator(g).ApplyHdrPostprocessToFinalSaves(
            clips,
            publication.SaveNodeIds);
    }

    private bool TryInstallRefineSourceVideo(IReadOnlyList<ClipSpec> clips)
    {
        if (!g.UserInput.TryGet(VideoStagesExtension.RefineSourceVideo, out Image refineSource)
            || refineSource is null
            || clips.Count == 0)
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

    private void EnsureComfyDependencies(IReadOnlyList<ClipSpec> clips)
    {
        if (g.Features.Contains(Constants.LtxVideoFeatureFlag)
            || !clips.Any(clip =>
                clip.HasIcLoras
                && clip.Stages.Any(stage => VideoStageModelCompat.IsLtxV2VideoModel(stage.Model))))
        {
            return;
        }

        throw new SwarmUserErrorException(
            "VideoStages IC-LoRAs require the ComfyUI-LTXVideo custom nodes. "
            + $"Install {Constants.LtxVideoNodeUrl} or use SwarmUI's LTXVideo feature installer.");
    }

}
