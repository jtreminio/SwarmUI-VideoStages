using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class Ltx2GenerationSession(
    WorkflowGenerator g,
    StageRefStore store,
    StageRunner stageRunner,
    AudioTimelineExecutor audioTimelineExecutor,
    StageGuideReferenceState guideReferences,
    BoundaryHandoffResolver boundaryHandoffResolver,
    StageSequenceRootSources rootSources,
    ArchitectureTimelineSessionContext session) : IVideoGenerationSession
{
    private readonly InitVideoClipInstaller _initVideoClipInstaller = new(g);
    private readonly GlobalVideoFrameTrimmer _trimmer = new(g);
    private readonly StageHostExecutionScope _stageScope = new(g, session.Plan);

    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        guideReferences.BeginClip();
        ClipPlan clip = context.Clip;
        ClipContext clipContext = new(
            session.Plan,
            clip,
            rootSources.SourceMedia,
            rootSources.SourceVae);

        WGNodeData initVideoMedia = InstallSourceIfPlanned(clip);
        clipContext.IcLoraEntryIncomingMedia =
            initVideoMedia
            ?? context.PreviousTimelineClipOutput?.ToHostMedia(g)
            ?? rootSources.SourceMedia;
        LtxBoundaryAudioCarry boundaryAudioCarry =
            PrepareCrossClipInput(context, initVideoMedia, clipContext);
        if (initVideoMedia is not null)
        {
            g.CurrentMedia = initVideoMedia;
        }

        audioTimelineExecutor.PrepareClipAudio(
            context,
            clipContext,
            initVideoMedia,
            rootSources.AudioSources,
            boundaryAudioCarry);
        RuntimeArtifact output = CaptureRuntimeArtifact();
        foreach (StagePlan stage in clip.Stages)
        {
            _stageScope.PublishStageInput(output);
            ExecuteStage(context, stage, clipContext);
            output = CaptureRuntimeArtifact();
            if (!output.HasMedia)
            {
                throw VideoStagesInvariant.Failure(
                    $"VideoStages: stage {stage.StageId} produced no media artifact.");
            }
            if (stage.Output.IsTimelineTerminal && _trimmer.IsRequested)
            {
                _trimmer.Apply();
                output = CaptureRuntimeArtifact();
            }
            _stageScope.PublishIntermediate(stage, g.CurrentMedia, g.CurrentVae);
        }
        return DecodedClipArtifact.FromRuntime(output, clip);
    }

    public void Dispose() => _stageScope.Dispose();

    private WGNodeData InstallSourceIfPlanned(ClipPlan clip)
    {
        if (clip.EntryMode != ArchitectureEntryMode.InitVideo)
        {
            return null;
        }
        WGNodeData initVideoMedia = _initVideoClipInstaller.TryInstall(clip);
        if (initVideoMedia is null)
        {
            throw VideoStagesInvariant.Failure(
                $"VideoStages: clip {clip.ClipId} source video could not be installed.");
        }
        return initVideoMedia;
    }

    private LtxBoundaryAudioCarry PrepareCrossClipInput(
        ArchitectureClipRuntimeContext context,
        WGNodeData initVideoMedia,
        ClipContext clipContext)
    {
        if (session.Plan.Clips.Count <= 1 || context.ClipIndex == 0)
        {
            return null;
        }

        if (initVideoMedia is null)
        {
            if (clipContext.SourceMedia is null)
            {
                throw VideoStagesInvariant.Failure(
                    $"VideoStages: clip {context.Clip.ClipId} requires root media before its first stage.");
            }
            g.CurrentMedia = clipContext.SourceMedia.Duplicate();
            if (clipContext.SourceVae is not null)
            {
                g.CurrentVae = clipContext.SourceVae.Duplicate();
            }
        }

        if (context.PreviousClip is null)
        {
            return null;
        }

        return boundaryHandoffResolver.Resolve(
            session.Assembly,
            context.PreviousClip,
            context.PreviousClipOutput?.ToHostMedia(g),
            context.Clip,
            clipContext);
    }

    private void ExecuteStage(
        ArchitectureClipRuntimeContext context,
        StagePlan stage,
        ClipContext clipContext)
    {
        StageRefStore.StageRef guideRef = guideReferences.Resolve(stage);
        int sectionId = _stageScope.ApplyStageOverrides(
            context.Clip,
            stage,
            clipContext.Dimensions.Width,
            clipContext.Dimensions.Height,
            clipContext.GenerationFrames);
        // Multi-clip runs first fork the shared root decode. Later compatible stages retarget that
        // clip-local branch, so clip sources stay independent without adding a decode per stage.
        bool requiresDedicatedOutput = _stageScope.PublishesIntermediateStages
            || (session.Plan.Clips.Count > 1
                && context.Clip.Stages
                    .FirstOrDefault(candidate => !candidate.IsPassthrough)?.StageId
                    == stage.StageId);
        stageRunner.RunStage(
            stage,
            sectionId,
            guideRef,
            store,
            clipContext,
            requiresDedicatedOutput,
            session.Plan.Root);
        guideReferences.CaptureStageOutput(stage);
    }

    private RuntimeArtifact CaptureRuntimeArtifact()
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        return RuntimeArtifact.Capture(g, bridge);
    }
}
