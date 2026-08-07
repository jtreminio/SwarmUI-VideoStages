using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Audio;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class StageSequenceRootSetup(
    WorkflowGenerator g,
    StageRefStore store,
    RootVideoStageResizer rootVideoStageResizer)
{
    public StageSequenceRootSources Prepare(
        AudioRuntimeSources preparedAudioSources,
        RootPlan root)
    {
        ArgumentNullException.ThrowIfNull(preparedAudioSources);
        ArgumentNullException.ThrowIfNull(root);

        if (root.UsesStageHandoff
            || root.DropsTextToVideoRootDonor
            || root.UsesGeneratedClipDonor)
        {
            rootVideoStageResizer.ApplyConfiguredRootStageDimensionsToCurrentMedia();
        }

        if (root.DropsTextToVideoRootDonor)
        {
            // Do not let replacement clips retain the unrelated root sampler through its audio.
            if (g.CurrentMedia is not null)
            {
                g.CurrentMedia.AttachedAudio = null;
            }
        }

        CaptureGeneratedReference(root);
        return new StageSequenceRootSources(
            g.CurrentMedia?.Duplicate(),
            g.CurrentVae?.Duplicate(),
            new AudioRuntimeSources(
                root.DropsTextToVideoRootDonor
                    ? null
                    : preparedAudioSources.NativeAudio ?? g.CurrentMedia?.AttachedAudio,
                preparedAudioSources.ClipAudios,
                preparedAudioSources.UploadedAudios));
    }

    public StageSequenceRootSources Snapshot(
        AudioRuntimeSources preparedAudioSources,
        RootPlan root)
    {
        ArgumentNullException.ThrowIfNull(preparedAudioSources);
        ArgumentNullException.ThrowIfNull(root);
        if (!root.TakesOverTextToVideoRoot)
        {
            store.Capture(StageRefStore.StageKind.Generated, g.CurrentMedia, g.CurrentVae);
        }
        return new StageSequenceRootSources(
            g.CurrentMedia?.Duplicate(),
            g.CurrentVae?.Duplicate(),
            preparedAudioSources);
    }

    private void CaptureGeneratedReference(RootPlan root)
    {
        if (root.TakesOverTextToVideoRoot)
        {
            return;
        }
        StageRefStore.StageRef reference = store.CaptureCurrentOutputReference();
        store.Capture(
            StageRefStore.StageKind.Generated,
            reference.Media,
            reference.Vae);
    }
}
