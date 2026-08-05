using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo.Runtime;
using VideoStages.Planning;

namespace VideoStages.Architectures.HostVideo;

internal sealed class HostVideoExecutionAdapter(WorkflowGenerator generator) :
    IArchitectureGenerationSessionProvider
{
    public ArchitectureId ArchitectureId =>
        HostVideoArchitectureModule.ArchitectureId;

    public IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RootOwnerArchitectureId != ArchitectureId)
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
        if (generator.UserInput.TryGet(T2IParamTypes.PromptAudios, out List<AudioFile> promptAudios)
            && promptAudios.Count > 0)
        {
            diagnostics.Add(Ignored(
                "host-video.audio-reference.ignored",
                "'Prompt Audios' is an architecture-specific enhancement and was "
                    + "ignored by the generic host-video fallback."));
        }
        if (generator.UserInput.TryGet(T2IParamTypes.PromptVideos, out List<VideoFile> promptVideos)
            && promptVideos.Count > 0)
        {
            diagnostics.Add(Ignored(
                "host-video.video-reference.ignored",
                "'Prompt Videos' is an architecture-specific enhancement and was "
                    + "ignored by the generic host-video fallback."));
        }
        return diagnostics;
    }

    public IVideoGenerationSession CreateSession(
        ArchitectureTimelineSessionContext context) =>
        StockHostVideoGenerationSession.Create(
            generator,
            context,
            ArchitectureId,
            "generic host");

    private static PlanDiagnostic Ignored(string code, string message) =>
        new(PlanDiagnosticSeverity.Warning, code, message);
}

/// <summary>
/// Keeps request-global host video settings out of the discarded core pass without changing
/// authored stage passes.
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
        if (!RequestCaches.TryGetActiveCoreVideoContext(
                genInfo,
                out WorkflowGenerator generator,
                out VideoExecutionPlanContext context))
        {
            return;
        }
        if (context.RootOwnerArchitectureId
            != HostVideoArchitectureModule.ArchitectureId)
        {
            return;
        }

        context.ExecutePrepared(() =>
        {
            genInfo.VideoSwapModel = null;
            genInfo.VideoSwapPercent = 0.5;
            genInfo.VideoEndFrame = null;
            genInfo.StartStep = 0;

            // Reuse the live base model state so the discarded core pass never loads the selected
            // video checkpoint, reads its audio-reference options, or leaves a video audio VAE behind.
            genInfo.Model = generator.CurrentModel
                ?? throw Invariant.Failure(
                    "The generic host-video core pass has no live base model.");
            genInfo.Vae = generator.CurrentVae
                ?? throw Invariant.Failure(
                    "The generic host-video core pass has no live base VAE.");
            genInfo.PosCond = generator.FinalPrompt;
            genInfo.NegCond = generator.FinalNegativePrompt;
            genInfo.HasMatchedModelData = true;
        });
    }
}
