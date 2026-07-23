using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Wraps the existing serial LTX collaborators behind the generic session contract.</summary>
internal sealed class Ltx2GenerationSession(
    WorkflowGenerator generator,
    StageClipExecutor executor,
    StageSequenceRootSources rootSources,
    StageHostExecutionScope hostScope) : IVideoGenerationSession
{
    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

    public ArchitectureClipExecutionRequest CreateExecutionRequest(
        ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(
            context.Clip,
            context.ClipIndex,
            new StageClipExecutionContext(
                context.Clip,
                context.Plan,
                context.ClipIndex,
                context.ParallelMultiClip,
                context.HasPreviousTimelineClip,
                context.PreviousClip,
                ToNodeData(context.PreviousClipOutput),
                rootSources,
                context.Assembly,
                hostScope,
                context.RootPolicy));
    }

    public DecodedClipArtifact Execute(ArchitectureClipExecutionRequest request)
    {
        if (request.Clip.ArchitecturePayload is not Ltx2ClipPayload)
        {
            throw new InvalidOperationException(
                $"Clip {request.Clip.ClipId} has no LTX architecture payload.");
        }
        StageClipExecutionContext context =
            request.RuntimePayload as StageClipExecutionContext
            ?? throw new InvalidOperationException("LTX execution payload is invalid.");
        RuntimeArtifact output = executor.Execute(context);
        return DecodedClipArtifact.FromRuntime(output, request.Clip);
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
