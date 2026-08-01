using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.Execution;

/// <summary>
/// Publishes pre-existing footage without invoking a generation stage.
/// </summary>
internal sealed class SourceOnlyGenerationSession(
    WorkflowGenerator generator,
    int framesPerSecond,
    AudioRuntimeSources audioSources) : IVideoGenerationSession
{
    private readonly InitVideoClipInstaller _sourceInstaller = new(generator);
    private readonly SourceOnlyClipAudioPreparer _audio = new(generator);

    public ArchitectureId ArchitectureId => NoneArchitecture.Id;

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Clip.ArchitecturePayload is not NoneClipPayload)
        {
            throw new InvalidOperationException(
                $"Clip {context.Clip.ClipId} has no init-video-only architecture payload.");
        }
        WGNodeData initVideoMedia = _sourceInstaller.TryInstall(context.Clip)
            ?? throw VideoStagesInvariant.Failure(
                $"VideoStages: clip {context.Clip.ClipId} source video could not be installed.");
        generator.CurrentMedia = initVideoMedia;
        _audio.Prepare(context.Clip, framesPerSecond, audioSources, initVideoMedia);
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        RuntimeArtifact output = RuntimeArtifact.Capture(
            generator,
            bridge);
        return DecodedClipArtifact.FromRuntime(output, context.Clip);
    }
}
