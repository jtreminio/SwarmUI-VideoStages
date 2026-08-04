using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Applies the compiled root plan once, captures the generated reference at the
/// post-video-chain boundary, and snapshots the root sources shared by generated clips.
/// </summary>
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

        if (root.UsesStageHandoff)
        {
            rootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia();
        }
        else if (root.DropsTextToVideoRootDonor)
        {
            // The root will be pruned, so only stamp its timeline dimensions. Removing inherited
            // audio prevents replacement clips from retaining the unrelated root sampler.
            rootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia();
            if (g.CurrentMedia is not null)
            {
                g.CurrentMedia.AttachedAudio = null;
            }
        }
        else if (root.UsesGeneratedClipDonor)
        {
            rootVideoStageResizer.ApplyConfiguredRootStageResolutionToSurvivingRootMedia();
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
        if (!root.DiscardsTextToVideoRoot)
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
        if (root.DiscardsTextToVideoRoot)
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
