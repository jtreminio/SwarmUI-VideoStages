using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// LTX execution state composed from the common per-clip facts, timeline-scoped LTX collaborators,
/// and decoded continuity media projected into host node data.
/// </summary>
internal sealed record StageClipExecutionContext(
    ArchitectureClipRuntimeContext Runtime,
    VideoExecutionPlan Plan,
    WGNodeData PreviousClipOutput,
    WGNodeData PreviousTimelineClipOutput,
    StageSequenceRootSources RootSources,
    TimelineAssemblySession Assembly,
    StageHostExecutionScope HostScope,
    StageExecutionOptions ExecutionOptions,
    RootExecutionPolicy RootPolicy);

/// <summary>Executes one planned clip and returns its terminal runtime artifact.</summary>
internal sealed class StageClipExecutor(
    WorkflowGenerator g,
    StageRefStore store,
    StageRunner singleStageRunner,
    AudioTimelineExecutor audioTimelineExecutor,
    StageGuideReferenceState guideReferences,
    BoundaryHandoffResolver boundaryHandoffResolver)
{
    private readonly SourcedClipInstaller _sourcedClipInstaller = new(g);

    public RuntimeArtifact Execute(StageClipExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        guideReferences.BeginClip();
        ClipPlan plannedClip = context.Runtime.Clip;
        ClipContext clipContext = new(
            context.Plan,
            plannedClip,
            context.RootSources.SourceMedia,
            context.RootSources.SourceVae);

        WGNodeData sourcedMedia = InstallSourceIfPlanned(plannedClip);
        clipContext.IcLoraEntryIncomingMedia =
            sourcedMedia
            ?? context.PreviousTimelineClipOutput
            ?? context.RootSources.SourceMedia;
        LtxBoundaryAudioCarry boundaryAudioCarry =
            PrepareCrossClipInput(context, sourcedMedia, clipContext);
        if (sourcedMedia is not null)
        {
            g.CurrentMedia = sourcedMedia;
        }

        PrepareClipAudio(context, clipContext, sourcedMedia, boundaryAudioCarry);
        if (plannedClip.Stages.Count == 0)
        {
            return CaptureStageInputArtifact(ArtifactOrigin.SourceVideo);
        }

        RuntimeArtifact clipArtifact = null;
        foreach (StagePlan plannedStage in plannedClip.Stages)
        {
            clipArtifact = ExecuteStage(
                context,
                plannedStage,
                clipContext,
                clipArtifact);
        }
        return clipArtifact;
    }

    private WGNodeData InstallSourceIfPlanned(ClipPlan plannedClip)
    {
        if (!plannedClip.IsSourced)
        {
            return null;
        }
        WGNodeData sourcedMedia = _sourcedClipInstaller.TryInstall(plannedClip);
        if (sourcedMedia is null)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: clip {plannedClip.ClipId} source video could not be installed.");
        }
        return sourcedMedia;
    }

    private LtxBoundaryAudioCarry PrepareCrossClipInput(
        StageClipExecutionContext context,
        WGNodeData sourcedMedia,
        ClipContext clipContext)
    {
        if (context.Plan.Clips.Count <= 1 || context.Runtime.ClipIndex == 0)
        {
            return null;
        }

        if (sourcedMedia is null)
        {
            if (clipContext.SourceMedia is null)
            {
                throw new SwarmUserErrorException(
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
            nextClipIsSourced: sourcedMedia is not null,
            clipContext);
    }

    private void PrepareClipAudio(
        StageClipExecutionContext context,
        ClipContext clipContext,
        WGNodeData sourcedMedia,
        LtxBoundaryAudioCarry boundaryAudioCarry)
    {
        ClipPlan plannedClip = context.Runtime.Clip;
        StagePlan firstStage = plannedClip.Stages.FirstOrDefault();
        audioTimelineExecutor.ApplyControlNetClipLength(plannedClip);
        AudioRuntimeSources clipAudioSources =
            sourcedMedia?.AttachedAudio is WGNodeData sourcedAudio
                ? context.RootSources.AudioSources with { NativeAudio = sourcedAudio }
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

    private RuntimeArtifact ExecuteStage(
        StageClipExecutionContext context,
        StagePlan plannedStage,
        ClipContext clipContext,
        RuntimeArtifact priorArtifact)
    {
        StageRefStore.StageRef guideRef = guideReferences.Resolve(plannedStage);
        if (guideRef?.Media is null)
        {
            Ltx2StagePayload payload = plannedStage.RequireLtx2Payload();
            throw new SwarmUserErrorException(
                $"VideoStages: Clip {context.Runtime.Clip.ClipId} stage {plannedStage.ClipStageIndex} "
                + $"could not resolve ImageReference '{payload.Guide.RawValue}'.");
        }

        int sectionId = context.HostScope.ApplyStageOverrides(
            context.Runtime.Clip,
            plannedStage,
            clipContext.Dimensions.Width,
            clipContext.Dimensions.Height);
        RuntimeArtifact inputArtifact = priorArtifact ?? CaptureStageInputArtifact(
            context.Runtime.Clip.IsSourced
                ? ArtifactOrigin.SourceVideo
                : ArtifactOrigin.HostRoot);
        context.HostScope.PublishStageInput(inputArtifact);
        RuntimeArtifact output = singleStageRunner.RunStage(
            plannedStage,
            sectionId,
            guideRef,
            store,
            clipContext,
            context.ExecutionOptions,
            context.RootPolicy);
        guideReferences.CaptureStageOutput(plannedStage);
        context.HostScope.PublishIntermediate(plannedStage);
        return output;
    }

    private RuntimeArtifact CaptureStageInputArtifact(ArtifactOrigin origin)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        return RuntimeArtifact.Capture(g, bridge, origin);
    }
}
