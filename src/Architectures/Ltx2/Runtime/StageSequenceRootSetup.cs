using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Applies the compiled root ownership policy once, captures the generated reference at the
/// post-video-chain boundary, and snapshots the root sources shared by generated clips.
/// </summary>
internal sealed class StageSequenceRootSetup(
    WorkflowGenerator g,
    StageRefStore store,
    RootVideoStageResizer rootVideoStageResizer)
{
    public StageSequenceRootSources Prepare(
        AudioRuntimeSources preparedAudioSources,
        RootExecutionPolicy rootPolicy)
    {
        ArgumentNullException.ThrowIfNull(preparedAudioSources);
        ArgumentNullException.ThrowIfNull(rootPolicy);

        if (rootPolicy.UsesStageHandoff)
        {
            rootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia();
        }
        else if (rootPolicy.DropsTextToVideoRootDonor)
        {
            // The root will be pruned, so only stamp its timeline dimensions. Removing inherited
            // audio prevents replacement clips from retaining the unrelated root sampler.
            rootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia();
            if (g.CurrentMedia is not null)
            {
                g.CurrentMedia.AttachedAudio = null;
            }
        }
        else if (rootPolicy.ConformsSurvivingRootMedia)
        {
            rootVideoStageResizer.ApplyConfiguredRootStageResolutionToSurvivingRootMedia();
        }

        CaptureGeneratedReference(rootPolicy);
        return new StageSequenceRootSources(
            g.CurrentMedia?.Duplicate(),
            g.CurrentVae?.Duplicate(),
            new AudioRuntimeSources(
                rootPolicy.DropsTextToVideoRootDonor
                    ? null
                    : preparedAudioSources.NativeAudio ?? g.CurrentMedia?.AttachedAudio,
                preparedAudioSources.ClipAudios,
                preparedAudioSources.UploadedAudios));
    }

    public StageSequenceRootSources Snapshot(
        AudioRuntimeSources preparedAudioSources,
        RootExecutionPolicy rootPolicy)
    {
        ArgumentNullException.ThrowIfNull(preparedAudioSources);
        ArgumentNullException.ThrowIfNull(rootPolicy);
        if (!rootPolicy.DiscardsTextToVideoRoot)
        {
            store.Capture(StageRefStore.StageKind.Generated, g.CurrentMedia, g.CurrentVae);
        }
        return new StageSequenceRootSources(
            g.CurrentMedia?.Duplicate(),
            g.CurrentVae?.Duplicate(),
            preparedAudioSources);
    }

    private void CaptureGeneratedReference(RootExecutionPolicy rootPolicy)
    {
        if (rootPolicy.DiscardsTextToVideoRoot)
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
