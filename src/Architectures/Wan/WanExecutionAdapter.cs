using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution.StockHost;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

internal sealed class WanExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionProvider
{
    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;

    /// <summary>
    /// Reports request-global host video parameters that cannot be applied to the authored Wan
    /// timeline before any host graph phase runs.
    /// </summary>
    public IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<PlanDiagnostic> diagnostics = [];
        if (generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null) is not null)
        {
            if (!WanVideoEndFramePolicy.TryResolveTarget(
                    context.Plan,
                    out _,
                    out _))
            {
                string families = string.Join(
                    ", ",
                    context.Plan.Clips
                        .Select(clip => clip.Architecture?.Id.ToString() ?? "<unresolved>")
                        .Distinct());
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "wan.end-frame.ignored",
                    "'Video End Frame' was ignored because it can only target the final "
                        + "generating stage of one WAN clip, and that stage must "
                        + "use a WAN model whose host workflow supports a final image. "
                        + $"This request has {context.Plan.Clips.Count} clip(s) across "
                        + $"architecture(s): {families}."));
            }
        }
        if (generator.UserInput.TryGet(
                T2IParamTypes.Video2VideoCreativity,
                out double creativity)
            && creativity != 1)
        {
            diagnostics.Add(Warn(
                "'Video2Video Creativity' is request-global, but Wan refinement strength is "
                + "clip-local. Use init-video stage 0 or each later stage's authored 'Control' "
                + "value instead."));
        }
        return diagnostics;
    }

    public IVideoGenerationSession CreateSession(
        ArchitectureTimelineSessionContext context) =>
        StockHostVideoGenerationSession.Create(
            generator,
            context,
            WanArchitectureModule.Instance.Descriptor,
            new WanStockHostVideoBehavior(generator, context.Plan));

    private static PlanDiagnostic Warn(
        string message,
        int? clipId = null,
        int? stageId = null) => new(
        PlanDiagnosticSeverity.Warning,
        "wan22.host-param.unsupported",
        message,
        clipId,
        stageId);
}
