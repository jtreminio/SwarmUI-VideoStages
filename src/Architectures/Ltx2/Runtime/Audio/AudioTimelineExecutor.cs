using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Coordinates root and per-clip audio preparation for the LTX timeline.
/// </summary>
internal sealed class AudioTimelineExecutor
{
    private readonly WorkflowGenerator _generator;
    private readonly LtxAudioInjector _audioInjector;
    private readonly ClipAudioPreparer _clipAudio;

    public AudioTimelineExecutor(
        WorkflowGenerator generator,
        LtxAudioInjector audioInjector)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(audioInjector);
        _generator = generator;
        _audioInjector = audioInjector;
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
        ApplyControlNetClipLength(first);
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

    public void ApplyControlNetClipLength(ClipPlan plannedClip)
    {
        ArgumentNullException.ThrowIfNull(plannedClip);
        if (plannedClip.Audio.Length.Owner != AudioLengthOwner.ControlNet
            || plannedClip.Stages.Count == 0)
        {
            return;
        }

        int sourceIndex = (plannedClip.ArchitecturePayload as IArchitectureControlNetSourcePlan)
            ?.ControlNetSourceIndex
            ?? throw new SwarmUserErrorException(
                "VideoStages: ControlNet owns clip length, but the compiled plan has no valid "
                + "ControlNet 1-3 source.");
        if (!TryApplyControlNetFrameCount(sourceIndex))
        {
            throw new SwarmUserErrorException(
                $"VideoStages: ControlNet {sourceIndex + 1} owns clip {plannedClip.ClipId} length, "
                + "but its captured video frame count is unavailable.");
        }
    }

    private bool TryApplyControlNetFrameCount(int controlNetSourceIndex)
    {
        if (!new LtxControlNetMediaNormalizer(_generator).TryCreateFrameCount(
                controlNetSourceIndex,
                out JArray framesConnection))
        {
            return false;
        }
        LtxFrameCountConnector.ApplyToExistingSources(_generator, framesConnection);
        return true;
    }
}
