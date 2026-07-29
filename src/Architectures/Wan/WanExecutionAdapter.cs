using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
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
    /// Host video parameters this slice cannot honor. They are consumed by the host's
    /// image-to-video construction or post-processing rather than the authored clip compiler, and
    /// each changes the result enough that silently omitting it would be the wrong answer.
    /// </summary>
    public IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<PlanDiagnostic> diagnostics = [];
        if (generator.UserInput.Get(T2IParamTypes.VideoSwapModel, null) is not null)
        {
            diagnostics.Add(Refuse(
                "'Video Swap Model' is set, but VideoStages does not yet run Wan's low-noise "
                + "second pass. Clear it to generate with the high-noise model alone."));
        }
        if (generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null) is not null)
        {
            diagnostics.Add(Refuse(
                "'Video End Frame' is set, but VideoStages does not yet support Wan's "
                + "first-to-last-frame generation."));
        }
        if (generator.UserInput.TryGet(
                T2IParamTypes.Video2VideoCreativity,
                out double creativity)
            && creativity != 1)
        {
            diagnostics.Add(Refuse(
                "'Video2Video Creativity' is non-default, but VideoStages does not yet support "
                + "Wan partial-denoise generation."));
        }
        if (generator.UserInput.TryGet(
                ComfyUIBackendExtension.VideoFrameInterpolationMethod,
                out string _)
            && generator.UserInput.TryGet(
                ComfyUIBackendExtension.VideoFrameInterpolationMultiplier,
                out int multiplier)
            && multiplier > 1)
        {
            diagnostics.Add(Refuse(
                "host frame interpolation is active, but VideoStages does not yet apply it to "
                + "the assembled Wan timeline."));
        }
        return diagnostics;
    }

    public void ExecuteHostPhase(ArchitectureHostPhaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        WanRootMediaHandoff handoff = new(generator);
        switch (context.Phase)
        {
            case ArchitectureHostPhase.CapturePreCoreMedia:
                handoff.CapturePreCoreMedia();
                break;
            case ArchitectureHostPhase.DropCoreOutput:
                handoff.DropCoreOutput();
                break;
            // Wan generates no audio, and refuses every stage image reference but the host root,
            // so it captures nothing at the reference and ControlNet phases.
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

    private static PlanDiagnostic Refuse(string message) => new(
        PlanDiagnosticSeverity.Error,
        "wan22.host-param.unsupported",
        message);
}
