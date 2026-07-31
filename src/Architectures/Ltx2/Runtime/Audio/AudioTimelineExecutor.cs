using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Coordinates root and per-clip audio preparation for the LTX timeline.
/// </summary>
internal sealed class AudioTimelineExecutor
{
    private readonly LtxAudioInjector _audioInjector;
    private readonly ClipAudioPreparer _clipAudio;
    private readonly ControlNetClipLengthApplicator _controlNetLength;

    public AudioTimelineExecutor(
        WorkflowGenerator generator,
        LtxAudioInjector audioInjector)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(audioInjector);
        _audioInjector = audioInjector;
        _controlNetLength = new ControlNetClipLengthApplicator(generator);
        _clipAudio = new ClipAudioPreparer(generator, audioInjector);
    }

    public void PrepareRootAudio(
        VideoExecutionPlan plan,
        AudioRuntimeSources sources,
        RootExecutionPolicy rootPolicy)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(rootPolicy);
        if (plan.Clips.Count == 0)
        {
            _ = _audioInjector.TryInject(sources.NativeAudio);
            return;
        }
        if (rootPolicy.UsesStageHandoff || rootPolicy.FirstClipIsSourced)
        {
            return;
        }

        ClipPlan first = plan.Clips[0];
        _controlNetLength.Apply(first);
        if (!first.Audio.Segments.Items.IsDefaultOrEmpty)
        {
            return;
        }
        WGNodeData audio = PlannedAudioSourceSelector.Select(
            first.ClipId,
            first.Audio.Base,
            sources,
            suppressNative: false);
        _ = _audioInjector.TryInject(
            audio,
            first.RequireLtx2Payload().AudioInjection.NonHandoffMatchesAudioLength);
    }

    public void PrepareClipAudio(
        ClipAudioExecutionContext context,
        ClipContext clipContext,
        LtxBoundaryAudioCarry boundaryAudioCarry = null) =>
        _clipAudio.Prepare(context, clipContext, boundaryAudioCarry);

    public void ApplyControlNetClipLength(ClipPlan plannedClip) =>
        _controlNetLength.Apply(plannedClip);
}
