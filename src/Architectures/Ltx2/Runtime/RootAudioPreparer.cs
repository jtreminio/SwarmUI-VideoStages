using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Applies the one root-level audio injection that can precede clip execution.</summary>
internal sealed class RootAudioPreparer(
    LtxAudioInjector audioInjector,
    ControlNetClipLengthApplicator controlNetLength)
{
    public void Prepare(
        VideoExecutionPlan plan,
        AudioRuntimeSources sources,
        RootExecutionPolicy rootPolicy)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(rootPolicy);
        if (plan.Clips.Count == 0)
        {
            _ = audioInjector.TryInject(sources.NativeAudio);
            return;
        }
        if (rootPolicy.UsesStageHandoff || rootPolicy.Facts.FirstClipIsSourced)
        {
            return;
        }

        ClipPlan first = plan.Clips[0];
        controlNetLength.Apply(first);
        if (!first.Audio.Segments.Items.IsDefaultOrEmpty)
        {
            return;
        }
        WGNodeData audio = PlannedAudioSourceSelector.Select(
            first.ClipId,
            first.Audio.Base,
            sources,
            suppressNative: false);
        _ = audioInjector.TryInject(
            audio,
            first.Audio.Length.NonHandoffInjectionMatchesAudioLength);
    }
}
