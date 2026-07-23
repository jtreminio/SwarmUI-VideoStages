using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Stable orchestration facade for the four focused per-clip audio runtime responsibilities.
/// </summary>
internal sealed class AudioTimelineExecutor
{
    private readonly RootAudioPreparer _rootAudio;
    private readonly ClipAudioPreparer _clipAudio;
    private readonly ControlNetClipLengthApplicator _controlNetLength;

    public AudioTimelineExecutor(
        WorkflowGenerator generator,
        LtxAudioInjector audioInjector)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(audioInjector);
        _controlNetLength = new ControlNetClipLengthApplicator(generator);
        _rootAudio = new RootAudioPreparer(audioInjector, _controlNetLength);
        _clipAudio = new ClipAudioPreparer(generator, audioInjector);
    }

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
