using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Wraps the existing serial LTX collaborators behind the generic session contract.</summary>
internal sealed class Ltx2GenerationSession(
    WorkflowGenerator generator,
    StageClipExecutor executor,
    StageSequenceRootSources rootSources,
    StageHostExecutionScope hostScope,
    VideoExecutionPlan plan,
    TimelineAssemblySession assembly,
    RootExecutionPolicy rootPolicy) : IVideoGenerationSession
{
    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Clip.ArchitecturePayload is not Ltx2ClipPayload)
        {
            throw new InvalidOperationException(
                $"Clip {context.Clip.ClipId} has no LTX architecture payload.");
        }
        StageClipExecutionContext stageContext = new(
            context,
            plan,
            ToNodeData(context.PreviousClipOutput),
            ToNodeData(context.PreviousTimelineClipOutput),
            rootSources,
            assembly,
            hostScope,
            rootPolicy);
        RuntimeArtifact output = executor.Execute(stageContext);
        return DecodedClipArtifact.FromRuntime(output, context.Clip);
    }

    public void Dispose()
    {
        hostScope.Dispose();
    }

    private WGNodeData ToNodeData(DecodedClipArtifact artifact)
    {
        if (artifact is null)
        {
            return null;
        }
        WGNodeData media = new(
            artifact.Video.ToPath(),
            generator,
            WGNodeData.DT_VIDEO,
            null)
        {
            Width = artifact.Width,
            Height = artifact.Height,
            Frames = artifact.Frames,
            FPS = artifact.FramesPerSecond,
        };
        if (artifact.Audio is not null)
        {
            media.AttachedAudio = new(
                artifact.Audio.ToPath(),
                generator,
                WGNodeData.DT_AUDIO,
                null);
        }
        return media;
    }
}
