using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.LTX2;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Stable orchestration facade for the four focused per-clip audio runtime responsibilities.
/// </summary>
internal sealed class AudioTimelineExecutor
{
    private readonly AudioRuntimeSourceResolver _sources;
    private readonly RootAudioPreparer _rootAudio;
    private readonly ClipAudioPreparer _clipAudio;
    private readonly ControlNetClipLengthApplicator _controlNetLength;

    public AudioTimelineExecutor(
        WorkflowGenerator generator,
        LtxManager ltxManager,
        AudioHandler audioHandler)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(ltxManager);
        ArgumentNullException.ThrowIfNull(audioHandler);
        _sources = new AudioRuntimeSourceResolver(generator, audioHandler);
        _controlNetLength = new ControlNetClipLengthApplicator(ltxManager);
        _rootAudio = new RootAudioPreparer(ltxManager, _controlNetLength);
        _clipAudio = new ClipAudioPreparer(generator, ltxManager);
    }

    public AudioRuntimeSources PrepareRuntimeSources(VideoExecutionPlan plan) =>
        _sources.Resolve(plan);

    public void PrepareRootAudio(
        VideoExecutionPlan plan,
        AudioRuntimeSources sources,
        RootExecutionPolicy rootPolicy) =>
        _rootAudio.Prepare(plan, sources, rootPolicy);

    public void PrepareClipAudio(ClipAudioExecutionContext context) =>
        _clipAudio.Prepare(context);

    public void ApplyControlNetClipLength(ClipPlan plannedClip) =>
        _controlNetLength.Apply(plannedClip);
}
