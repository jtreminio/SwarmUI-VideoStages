using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;

namespace VideoStages.Architectures.MiniMax;

internal sealed class MiniMaxExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionProvider
{
    private CapturedHostReference _baseReference;
    private CapturedHostReference _refinerReference;

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
        ClipPlan dynamicLengthClip = context.Plan.Clips.FirstOrDefault(
            clip => clip.Architecture.Id == ArchitectureId
                && clip.Audio.Length.Owner == AudioLengthOwner.Audio
                && clip.Architecture.AudioSourceKinds.Contains(clip.Audio.Base.Kind));
        if (dynamicLengthClip is not null && context.Plan.Clips.Count != 1)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Error,
                "minimax.audio-derived-duration.multi-clip-unsupported",
                "MiniMax audio-derived duration currently requires a single-clip timeline.",
                dynamicLengthClip.ClipId));
        }
        return diagnostics;
    }

    public void CaptureBaseReference(VideoExecutionPlan plan)
    {
        if (AnyClipReferences(plan, "Base"))
        {
            _baseReference = CapturedHostReference.From(generator);
        }
    }

    public void CaptureRefinerReference(VideoExecutionPlan plan)
    {
        if (AnyClipReferences(plan, "Refiner"))
        {
            _refinerReference = CapturedHostReference.From(generator);
        }
    }

    /// <summary>
    /// A capture pins the host node it names for the rest of the request, which is why an unwanted
    /// one is not free: it denies that node to anything that would otherwise take it over. Only
    /// capture what some clip actually asked to keyframe from.
    /// </summary>
    private static bool AnyClipReferences(VideoExecutionPlan plan, string source) =>
        plan.Clips
            .Select(clip => clip.ArchitecturePayload as MiniMaxClipPayload)
            .Where(payload => payload is not null)
            .SelectMany(payload =>
                new[] { payload.FirstFrameReference, payload.LastFrameReference })
            .Any(reference => StringUtils.Equals(reference?.Source, source));

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
            context.Assembly,
            _baseReference,
            _refinerReference,
            new VideoStageRunner(
                generator,
                context.Plan,
                MiniMaxGenerationSession.ArchitectureLabel,
                preserveAttachedAudio: true),
            context.RootAdoption);
    }
}
