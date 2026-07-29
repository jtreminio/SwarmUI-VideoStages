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

        Dictionary<int, WanStagePayload> stages = [];
        foreach (StageSpec stage in clip.Stages ?? [])
        {
            // Wan enters from a still image, so a stage that generates nothing would hand a single
            // frame to timeline assembly instead of decoded video.
            Refuse(stage.IsPassthrough, "a stage that generates nothing", stage.Id);
            // Wan's only stage generates from the host image, which is a full generation. Partial
            // regeneration needs a prior video to denoise from, and this slice never has one.
            Refuse(
                !stage.IsPassthrough && stage.Control < 1,
                "partial regeneration",
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
