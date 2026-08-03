using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed record StageClipExecutionContext(
    ArchitectureClipRuntimeContext Runtime,
    VideoExecutionPlan Plan,
    WGNodeData PreviousClipOutput,
    WGNodeData PreviousTimelineClipOutput,
    StageSequenceRootSources RootSources,
    TimelineAssemblySession Assembly,
    RootExecutionPolicy RootPolicy);

internal sealed class StageClipExecutor(
    WorkflowGenerator g,
    StageRefStore store,
    StageRunner singleStageRunner,
    AudioTimelineExecutor audioTimelineExecutor,
    StageGuideReferenceState guideReferences,
    BoundaryHandoffResolver boundaryHandoffResolver)
{
    private readonly InitVideoClipInstaller _initVideoClipInstaller = new(g);

    public RuntimeArtifact Execute(
        StageClipExecutionContext context,
        VideoStageRunner stageRunner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stageRunner);
        guideReferences.BeginClip();
        ClipPlan plannedClip = context.Runtime.Clip;
        ClipContext clipContext = new(
            context.Plan,
            plannedClip,
            context.RootSources.SourceMedia,
            context.RootSources.SourceVae);

        WGNodeData initVideoMedia = InstallSourceIfPlanned(plannedClip);
        clipContext.IcLoraEntryIncomingMedia =
            initVideoMedia
            ?? context.PreviousTimelineClipOutput
            ?? context.RootSources.SourceMedia;
        LtxBoundaryAudioCarry boundaryAudioCarry =
            PrepareCrossClipInput(context, initVideoMedia, clipContext);
        if (initVideoMedia is not null)
        {
            g.CurrentMedia = initVideoMedia;
        }

        PrepareClipAudio(context, clipContext, initVideoMedia, boundaryAudioCarry);
        return stageRunner.ExecuteStages(plannedClip, (stage, continuation) =>
        {
            if (continuation is not null)
            {
                throw VideoStagesInvariant.Failure(
                    $"LTX stage {stage.StageId} cannot consume sampling continuation stage "
                        + $"{continuation.StageId}.");
            }
            ExecuteStage(context, stage, clipContext, stageRunner);
            return false;
        });
    }

    private WGNodeData InstallSourceIfPlanned(ClipPlan plannedClip)
    {
        if (!plannedClip.HasInitVideo)
        {
            return null;
        }
        WGNodeData initVideoMedia = _initVideoClipInstaller.TryInstall(plannedClip);
        if (initVideoMedia is null)
        {
            throw VideoStagesInvariant.Failure(
                $"VideoStages: clip {plannedClip.ClipId} source video could not be installed.");
        }
        return initVideoMedia;
    }

    private LtxBoundaryAudioCarry PrepareCrossClipInput(
        StageClipExecutionContext context,
        WGNodeData initVideoMedia,
        ClipContext clipContext)
    {
        if (context.Plan.Clips.Count <= 1 || context.Runtime.ClipIndex == 0)
        {
            return null;
        }

        if (initVideoMedia is null)
        {
            if (clipContext.SourceMedia is null)
            {
                throw VideoStagesInvariant.Failure(
                    $"VideoStages: clip {context.Runtime.Clip.ClipId} requires root media before its first stage.");
            }
            g.CurrentMedia = clipContext.SourceMedia.Duplicate();
            if (clipContext.SourceVae is not null)
            {
                g.CurrentVae = clipContext.SourceVae.Duplicate();
            }
        }

        if (context.Runtime.PreviousClip is null)
        {
            return null;
        }

        return boundaryHandoffResolver.Resolve(
            context.Assembly,
            context.Runtime.PreviousClip,
            context.PreviousClipOutput,
            context.Runtime.Clip,
            nextClipHasInitVideo: initVideoMedia is not null,
            clipContext);
    }

    private void PrepareClipAudio(
        StageClipExecutionContext context,
        ClipContext clipContext,
        WGNodeData initVideoMedia,
        LtxBoundaryAudioCarry boundaryAudioCarry)
    {
        ClipPlan plannedClip = context.Runtime.Clip;
        StagePlan firstStage = plannedClip.Stages.FirstOrDefault();
        audioTimelineExecutor.ApplyControlNetClipLength(plannedClip);
        AudioRuntimeSources clipAudioSources =
            initVideoMedia?.AttachedAudio is WGNodeData initVideoAudio
                ? context.RootSources.AudioSources with { NativeAudio = initVideoAudio }
                : context.RootSources.AudioSources;
        audioTimelineExecutor.PrepareClipAudio(new(
            firstStage,
            plannedClip,
            context.Plan.FramesPerSecond,
            IsFirstClip: context.Runtime.ClipIndex == 0,
            clipAudioSources,
            context.RootPolicy),
            clipContext,
            boundaryAudioCarry);
    }

    private void ExecuteStage(
        StageClipExecutionContext context,
        StagePlan plannedStage,
        ClipContext clipContext,
        VideoStageRunner stageRunner)
    {
        StageRefStore.StageRef guideRef = guideReferences.Resolve(plannedStage);
        int sectionId = stageRunner.ApplyStageOverrides(
            context.Runtime.Clip,
            plannedStage,
            clipContext.Dimensions.Width,
            clipContext.Dimensions.Height,
            clipContext.GenerationFrames);
        // Multi-clip runs first fork the shared root decode. Later compatible stages retarget that
        // clip-local branch, so clip sources stay independent without adding a decode per stage.
        bool requiresDedicatedOutput = stageRunner.PublishesIntermediateStages
            || (context.Plan.Clips.Count > 1
                && context.Runtime.Clip.Stages
                    .FirstOrDefault(candidate => !candidate.IsPassthrough)?.StageId
                    == plannedStage.StageId);
        singleStageRunner.RunStage(
            plannedStage,
            sectionId,
            guideRef,
            store,
            clipContext,
            requiresDedicatedOutput,
            context.RootPolicy);
        guideReferences.CaptureStageOutput(plannedStage);
    }

}
