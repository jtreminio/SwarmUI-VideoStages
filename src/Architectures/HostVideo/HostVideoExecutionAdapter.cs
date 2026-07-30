using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;

namespace VideoStages.Architectures.HostVideo;

/// <summary>Connects the proven stock-host fallback to common execution orchestration.</summary>
internal sealed class HostVideoExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionFactoryProvider,
    IArchitectureHostPhaseParticipant
{
    public ArchitectureId ArchitectureId =>
        HostVideoArchitectureModule.ArchitectureId;

    public IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ArchitectureRootOwnerResolver.Resolve(context.Plan) != ArchitectureId)
        {
            return [];
        }
        List<PlanDiagnostic> diagnostics = [];
        if (generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null) is not null)
        {
            diagnostics.Add(Ignored(
                "host-video.end-frame.ignored",
                "'Video End Frame' is not supported by the generic host-video fallback and was "
                    + "ignored. The authored request value remains unchanged."));
        }
        if (generator.UserInput.TryGet(
                T2IParamTypes.Video2VideoCreativity,
                out double creativity)
            && creativity != 1)
        {
            diagnostics.Add(Ignored(
                "host-video.creativity.ignored",
                "'Video2Video Creativity' is request-global and was ignored by the generic "
                    + "host-video fallback. Use each later stage's Control value instead."));
        }
        if (generator.UserInput.Get(T2IParamTypes.VideoAudioReference, null) is not null)
        {
            diagnostics.Add(Ignored(
                "host-video.audio-reference.ignored",
                "'Video Audio Reference' is an architecture-specific enhancement and was "
                    + "ignored by the generic host-video fallback."));
        }
        return diagnostics;
    }

    public void ExecuteHostPhase(ArchitectureHostPhaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HostVideoRootMediaHandoff handoff = new(
            generator,
            ArchitectureId,
            "generic host video");
        switch (context.Phase)
        {
            case ArchitectureHostPhase.CapturePreCoreMedia:
                handoff.CapturePreCoreMedia();
                break;
            case ArchitectureHostPhase.DropCoreOutput:
                handoff.DropCoreOutput();
                break;
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
        new HostVideoGenerationSessionFactory(generator);

    private static PlanDiagnostic Ignored(string code, string message) =>
        new(PlanDiagnosticSeverity.Warning, code, message);
}

/// <summary>
/// Keeps request-global host video settings out of the core pass that VideoStages discards.
/// Authored stage passes use non-core section IDs and are deliberately untouched.
/// </summary>
internal static class HostVideoCorePassIsolation
{
    private static int _registered;

    internal static void RegisterHandlers()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }
        WorkflowGenerator.AltImageToVideoPreHandlers.Add(Isolate);
    }

    internal static void Isolate(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator generator = genInfo?.Generator;
        if (generator is null
            || genInfo.ContextID != T2IParamInput.SectionID_Video
            || !VideoStagesPromptSection.IsActive(generator))
        {
            return;
        }
        VideoExecutionPlanContext context = generator.GetVideoExecutionPlanContext();
        if (context is null)
        {
            return;
        }
        if (!ArchitectureRootOwnerResolver.TryResolve(
                context.Plan,
                out ArchitectureId? rootOwner)
            || rootOwner != HostVideoArchitectureModule.ArchitectureId)
        {
            return;
        }

        context.ExecutePrepared(() =>
        {
            genInfo.VideoSwapModel = null;
            genInfo.VideoSwapPercent = 0.5;
            genInfo.VideoEndFrame = null;
            genInfo.StartStep = 0;

            // The core pass is only a donor that VideoStages drops immediately afterward. Reuse the
            // already-live base model state so this throwaway pass never loads the selected video
            // checkpoint, reads its audio-reference options, or leaves a video audio VAE behind.
            genInfo.Model = generator.CurrentModel
                ?? throw new InvalidOperationException(
                    "The generic host-video core pass has no live base model.");
            genInfo.Vae = generator.CurrentVae
                ?? throw new InvalidOperationException(
                    "The generic host-video core pass has no live base VAE.");
            genInfo.PosCond = generator.FinalPrompt;
            genInfo.NegCond = generator.FinalNegativePrompt;
            genInfo.HasMatchedModelData = true;
        });
    }
}

internal sealed class HostVideoGenerationSessionFactory(WorkflowGenerator generator) :
    IArchitectureGenerationSessionFactory
{
    private HostVideoRootSources _rootSources;

    public ArchitectureId ArchitectureId =>
        HostVideoArchitectureModule.ArchitectureId;

    public IArchitectureBoundaryAssembler BoundaryAssembler => null;

    public void PrepareTimeline(ArchitectureTimelinePreparationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _rootSources = new(
            generator.CurrentMedia?.Duplicate(),
            generator.CurrentVae?.Duplicate());
    }

    public IVideoGenerationSession CreateSession(
        ArchitectureTimelineSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_rootSources is null)
        {
            throw new InvalidOperationException(
                "The generic host-video runtime was not prepared before session creation.");
        }
        return new HostVideoGenerationSession(
            generator,
            context.Plan,
            _rootSources,
            new HostVideoStageEngine(
                generator,
                context.Plan,
                "generic host"));
    }

    public void FinalizeTimeline(
        ArchitectureTimelineFinalizationContext context)
    {
    }
}
