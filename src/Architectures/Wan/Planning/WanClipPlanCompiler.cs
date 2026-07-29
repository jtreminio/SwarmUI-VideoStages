using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

internal sealed record WanClipPlanCompilation(
    WanClipPayload Payload,
    IReadOnlyList<PlanDiagnostic> Diagnostics);

/// <summary>
/// Compiles the Wan-owned settings of one clip before the common clip plan is assembled. Settings
/// this slice cannot honor are refused here rather than dropped: a compiled payload is the whole
/// instruction, so anything it omits must have been rejected.
/// </summary>
internal static class WanClipPlanCompiler
{
    /// <summary>The only stage input Wan consumes: the media the host root produced.</summary>
    private const string GeneratedRootReference = "Generated";

    internal static WanClipPlanCompilation Compile(ClipSpec clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        List<PlanDiagnostic> diagnostics = [];
        void Refuse(bool configured, string option, int? stageId = null)
        {
            if (configured)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "wan22.option.unsupported",
                    $"Clip {clip.Id} configures '{option}', which architecture "
                        + $"'{WanArchitectureModule.ArchitectureId}' does not support.",
                    clip.Id,
                    stageId));
            }
        }

        // Wan produces no audio, so the common audio-length owner and LTX-style reuse have no
        // source to read. The capability validator only inspects the authored audio source string.
        Refuse(clip.ReuseAudio, "audio reuse");
        Refuse(clip.ClipLengthFromAudio, "clip length from audio");
        Refuse(clip.ClipLengthFromControlNet, "clip length from ControlNet");

        Dictionary<int, WanStagePayload> stages = [];
        foreach (StageSpec stage in clip.Stages ?? [])
        {
            // Wan enters from a still image, so a stage that generates nothing would hand a single
            // frame to timeline assembly instead of decoded video.
            Refuse(stage.IsPassthrough, "a stage that generates nothing", stage.Id);
            Refuse(
                !string.IsNullOrWhiteSpace(stage.ImageReference)
                    && !StringUtils.Equals(stage.ImageReference, GeneratedRootReference),
                $"stage image reference '{stage.ImageReference}'",
                stage.Id);
            stages.Add(
                stage.ClipStageRawIndex,
                new WanStagePayload(
                    stage.Model,
                    stage.Control,
                    stage.Steps,
                    stage.CfgScale,
                    stage.Sampler,
                    stage.Scheduler));
        }
        return new(new WanClipPayload(clip.Id, stages), diagnostics.AsReadOnly());
    }
}
