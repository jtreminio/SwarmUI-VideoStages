using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Execution;

internal sealed class SourceOnlyExecutionAdapter(
    WorkflowGenerator generator) :
    IArchitectureGenerationSessionFactoryProvider
{
    public ArchitectureId ArchitectureId => NoneArchitecture.Id;

    public IArchitectureGenerationSessionFactory CreateFactory() =>
        new SourceOnlyGenerationSessionFactory(generator);
}

internal sealed class SourceOnlyGenerationSessionFactory(
    WorkflowGenerator generator) : IArchitectureGenerationSessionFactory
{
    public ArchitectureId ArchitectureId => NoneArchitecture.Id;

    public IVideoGenerationSession CreateSession(ArchitectureTimelineSessionContext context) =>
        new SourceOnlyGenerationSession(
            generator,
            context.Plan.FramesPerSecond,
            context.AudioSources);
}

internal sealed class SourceOnlyGenerationSession(
    WorkflowGenerator generator,
    int framesPerSecond,
    AudioRuntimeSources audioSources) : IVideoGenerationSession
{
    private readonly InitVideoClipInstaller _sourceInstaller = new(generator);

    public ArchitectureId ArchitectureId => NoneArchitecture.Id;

    public DecodedClipArtifact Execute(ArchitectureClipRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Clip.ArchitecturePayload is not NoneClipPayload)
        {
            throw VideoStagesInvariant.Failure(
                $"Clip {context.Clip.ClipId} has no init-video-only architecture payload.");
        }
        WGNodeData initVideoMedia = _sourceInstaller.TryInstall(context.Clip)
            ?? throw VideoStagesInvariant.Failure(
                $"VideoStages: clip {context.Clip.ClipId} source video could not be installed.");
        generator.CurrentMedia = initVideoMedia;
        PrepareAudio(context.Clip, initVideoMedia);
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        RuntimeArtifact output = RuntimeArtifact.Capture(
            generator,
            bridge);
        return DecodedClipArtifact.FromRuntime(output, context.Clip);
    }

    private void PrepareAudio(ClipPlan clip, WGNodeData initVideoMedia)
    {
        AudioRuntimeSources sources = initVideoMedia.AttachedAudio is WGNodeData nativeAudio
            ? audioSources with { NativeAudio = nativeAudio }
            : audioSources;
        WGNodeData baseAudio = PlannedAudioSourceSelector.Select(
            clip.ClipId,
            clip.Audio.Base,
            sources,
            suppressNative: false);
        double duration = ClipAudioBedDuration.Seconds(
            clip,
            framesPerSecond,
            initVideoMedia);
        WGNodeData combinedAudio = new AudioSegmentCombiner(generator).Combine(
            clip.ClipId,
            clip.Audio.Segments,
            baseAudio,
            duration,
            out _);
        WGNodeData currentMedia = initVideoMedia.Duplicate();
        currentMedia.AttachedAudio = combinedAudio;
        generator.CurrentMedia = currentMedia;
    }
}
