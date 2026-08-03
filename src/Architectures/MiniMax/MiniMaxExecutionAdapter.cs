using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;

namespace VideoStages.Architectures.MiniMax;

internal sealed class MiniMaxExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionProvider,
    IArchitectureHostPhaseParticipant
{
    private readonly RootMediaHandoff _rootHandoff = new(
        generator,
        MiniMaxGenerationSession.ArchitectureLabel);
    private WGNodeData _baseReference;
    private WGNodeData _refinerReference;

    public ArchitectureId ArchitectureId => MiniMaxArchitectureModule.ArchitectureId;

    public IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<PlanDiagnostic> diagnostics = [];
        if (generator.UserInput.TryGet(
                T2IParamTypes.Video2VideoCreativity,
                out double creativity)
            && creativity != 1)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "minimax.host-param.unsupported",
                "'Video2Video Creativity' is ignored for MiniMax H3 clips: H3 conditions on "
                    + "keyframe images rather than on a denoise start step."));
        }
        if (generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null) is not null
            && context.Plan.Clips.Count != 1)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "minimax.end-frame.ignored",
                "'Video End Frame' was ignored because it has no unambiguous target in a "
                    + $"{context.Plan.Clips.Count}-clip timeline. Author a final-frame "
                    + "reference on the clip that needs one instead."));
        }
        return diagnostics;
    }

    public void ExecuteHostPhase(ArchitectureHostPhaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.Phase)
        {
            case ArchitectureHostPhase.CapturePreCoreMedia:
                _rootHandoff.CapturePreCoreMedia();
                break;
            case ArchitectureHostPhase.DropCoreOutput:
                _rootHandoff.DropCoreOutput();
                break;
            case ArchitectureHostPhase.CaptureBaseReference:
                _baseReference = generator.CurrentMedia?.Duplicate();
                break;
            case ArchitectureHostPhase.CaptureRefinerReference:
                _refinerReference = generator.CurrentMedia?.Duplicate();
                break;
            case ArchitectureHostPhase.ApplyRootAudioMaskDimensions:
            case ArchitectureHostPhase.CaptureControlNetPreprocessors:
                break;
        }
    }

    public IVideoGenerationSession CreateSession(
        ArchitectureTimelineSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HostVideoRootSources rootSources = new(
            generator.CurrentMedia?.Duplicate(),
            generator.CurrentVae?.Duplicate());
        return new MiniMaxGenerationSession(
            generator,
            context.Plan,
            rootSources,
            context.AudioSources,
            _baseReference,
            _refinerReference,
            new VideoStageRunner(
                generator,
                context.Plan,
                MiniMaxGenerationSession.ArchitectureLabel,
                preserveAttachedAudio: true));
    }
}
