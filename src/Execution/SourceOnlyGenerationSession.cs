using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Execution;

internal sealed record SourceOnlyClipExecutionContext(
    ClipPlan Clip,
    int ClipIndex,
    AudioRuntimeSources AudioSources);

/// <summary>
/// Executes pre-existing footage without constructing or consuming a generation runtime, latent, VAE,
/// stage runner, or generation-stage execution context.
/// </summary>
internal sealed class SourceOnlyGenerationSession(
    WorkflowGenerator generator) : IVideoGenerationSession
{
    private readonly SourcedClipInstaller _sourceInstaller = new(generator);
    private readonly SourceOnlyClipAudioPreparer _audio = new(generator);

    public ArchitectureId ArchitectureId => NoneArchitecture.Id;

    public ArchitectureClipExecutionRequest CreateExecutionRequest(
        ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(
            context.Clip,
            context.ClipIndex,
            new SourceOnlyClipExecutionContext(
                context.Clip,
                context.ClipIndex,
                context.AudioSources));
    }

    public DecodedClipArtifact Execute(ArchitectureClipExecutionRequest request)
    {
        if (request.Clip.ArchitecturePayload is not NoneClipPayload)
        {
            throw new InvalidOperationException(
                $"Clip {request.Clip.ClipId} has no sourced-only architecture payload.");
        }
        SourceOnlyClipExecutionContext context =
            request.RuntimePayload as SourceOnlyClipExecutionContext
            ?? throw new InvalidOperationException("Sourced-only execution payload is invalid.");
        WGNodeData sourcedMedia = _sourceInstaller.TryInstall(context.Clip)
            ?? throw new SwarmUserErrorException(
                $"VideoStages: clip {context.Clip.ClipId} source video could not be installed.");
        generator.CurrentMedia = sourcedMedia;
        _audio.Prepare(context, sourcedMedia);
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        RuntimeArtifact output = RuntimeArtifact.Capture(
            generator,
            bridge,
            ArtifactOrigin.SourceVideo);
        return DecodedClipArtifact.FromRuntime(output, request.Clip);
    }

    public void Dispose()
    {
    }
}
