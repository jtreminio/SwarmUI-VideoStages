using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.LTX2;

namespace VideoStages;

internal sealed record StageSequenceRootSources(
    WGNodeData SourceMedia,
    WGNodeData SourceVae,
    AudioRuntimeSources AudioSources);

/// <summary>
/// Applies the compiled root ownership policy once, captures the generated reference at the
/// post-video-chain boundary, and snapshots the root sources shared by generated clips.
/// </summary>
internal sealed class StageSequenceRootSetup(
    WorkflowGenerator g,
    StageRefStore store,
    RootVideoStageResizer rootVideoStageResizer,
    LtxManager ltxManager)
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

        CaptureGeneratedReference();
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

    private void CaptureGeneratedReference()
    {
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        ltxManager.ApplyPostVideoChainCaptureIfPresent(ref referenceMedia, ref referenceVae);
        store.Capture(StageRefStore.StageKind.Generated, referenceMedia, referenceVae);
    }
}
