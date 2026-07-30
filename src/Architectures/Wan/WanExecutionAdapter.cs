using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Wan-owned adapter for request preflight, host phases, and timeline-session construction.
/// </summary>
internal sealed class WanExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionFactoryProvider,
    IArchitectureHostPhaseParticipant
{
    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;

    /// <summary>
    /// Validates request-global host video parameters before any host graph phase runs. Parameters
    /// that do not belong to the authored WAN timeline are either projected to one warning and
    /// ignored, or refused when silently changing their meaning would be misleading.
    /// </summary>
    public IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<PlanDiagnostic> diagnostics = [];
        if (context.Plan.Root.HostKind == HostRootKind.GlobalRefineSource
            && context.Plan.Clips.Any(
                clip => clip.Architecture?.Id == ArchitectureId
                    && clip.EntryMode == ArchitectureEntryMode.SourceVideo
                    && clip.Input == ClipInputKind.SourceVideo
                    && clip.IsSourced
                    && clip.SourceVideo is not null))
        {
            diagnostics.Add(Refuse(
                "'Refine Source Video' is a request-global donor and cannot coexist with a "
                + "clip-local sourced Wan timeline."));
        }
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
                        + "generating stage of one WAN image-to-video clip, and that stage must "
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
            diagnostics.Add(Refuse(
                "'Video2Video Creativity' is request-global, but Wan refinement strength is "
                + "clip-local. Use sourced stage 0 or each later stage's authored 'Control' "
                + "value instead."));
        }
        return diagnostics;
    }

    public void ExecuteHostPhase(ArchitectureHostPhaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HostVideoRootMediaHandoff handoff = new(
            generator,
            WanArchitectureModule.ArchitectureId,
            "Wan");
        switch (context.Phase)
        {
            case ArchitectureHostPhase.CapturePreCoreMedia:
                handoff.CapturePreCoreMedia();
                break;
            case ArchitectureHostPhase.DropCoreOutput:
                handoff.DropCoreOutput();
                break;
            // PreviousStage is a Wan-local decoded handoff assembled by the session. It is not a
            // captured host reference, so Wan needs nothing from reference or ControlNet phases.
            case ArchitectureHostPhase.ApplyRootAudioMaskDimensions:
            case ArchitectureHostPhase.CaptureBaseReference:
            case ArchitectureHostPhase.CaptureRefinerReference:
            case ArchitectureHostPhase.CaptureControlNetPreprocessors:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(context));
        }
    }

    public IArchitectureGenerationSessionFactory CreateFactory() =>
        new WanGenerationSessionFactory(generator);

    private static PlanDiagnostic Refuse(
        string message,
        int? clipId = null,
        int? stageId = null) => new(
        PlanDiagnosticSeverity.Error,
        "wan22.host-param.unsupported",
        message,
        clipId,
        stageId);
}
