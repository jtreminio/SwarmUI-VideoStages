using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Execution.StockHost;
using VideoStages.Planning;

namespace VideoStages.Architectures.MiniMax;

internal sealed class MiniMaxSessionProvider(WorkflowGenerator generator) :
    IArchitectureGenerationSessionProvider
{
    private CapturedHostReference _baseReference;
    private CapturedHostReference _refinerReference;

    public ArchitectureId ArchitectureId => MiniMaxArchitectureModule.ArchitectureId;

    public IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        UploadedMediaPreflight media = new(generator.UserInput);
        List<PlanDiagnostic> diagnostics = [
            .. MiniMaxClipReferences.PreflightUploads(
                media,
                context.Plan)
        ];
        foreach (ClipPlan clip in context.Plan.Clips.Where(
            clip => clip.Architecture.Id == ArchitectureId))
        {
            MiniMaxClipPayload payload = clip.RequireMiniMaxPayload();
            if (payload.TextEncoder != MiniMaxTextEncoder.Default
                && !generator.Features.Contains(MiniMaxTextEncoderGraph.FeatureFlag))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "minimax.text-encoder.clipproj-required",
                    "MiniMax H3's 8B and 4B text encoders require ClipProj. Install "
                        + $"{MiniMaxTextEncoderGraph.NodeUrl} and restart ComfyUI.",
                    clip.ClipId));
            }
            foreach (StagePlan stage in clip.Stages)
            {
                foreach (FrameRefPlan reference in
                    stage.RequireStockHostVideoPayload(
                            ArchitectureId,
                            MiniMaxArchitectureModule.Instance.Descriptor.DisplayName)
                        .FrameReferences
                        .Where(reference =>
                            !reference.IsEndpoint
                            && reference.SourceKind == FrameRefSourceKind.Upload))
                {
                    if (media.ImageDiagnostic(
                        reference.InlineData,
                        reference.UploadFileName,
                        $"clip {clip.ClipId} keyframe",
                        clip.ClipId,
                        stage.StageId) is { } unreadable)
                    {
                        diagnostics.Add(unreadable);
                    }
                }
            }
        }
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
        if (generator.UserInput.Get(T2IParamTypes.VideoEndImage, null) is not null
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
                && clip.Audio.LengthOwner == AudioLengthOwner.Audio
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
        if (AnyClipReferences(plan, MediaSource.Base))
        {
            _baseReference = CapturedHostReference.From(generator);
        }
    }

    public void CaptureRefinerReference(VideoExecutionPlan plan)
    {
        if (AnyClipReferences(plan, MediaSource.Refiner))
        {
            _refinerReference = CapturedHostReference.From(generator);
        }
    }

    /// <summary>
    /// A capture pins the host node it names for the rest of the request, which is why an unwanted
    /// one is not free: it denies that node to anything that would otherwise take it over. Only
    /// capture a source some clip names, whether as a keyframe or as a clip reference.
    /// </summary>
    private static bool AnyClipReferences(VideoExecutionPlan plan, string source) =>
        plan.Clips
            .Where(clip => clip.Architecture.Id == MiniMaxArchitectureModule.ArchitectureId)
            .SelectMany(clip =>
            {
                MiniMaxClipPayload payload = clip.RequireMiniMaxPayload();
                return new[]
                {
                    payload.FirstFrameReference?.Source,
                    payload.LastFrameReference?.Source,
                }
                    .Concat(payload.References.Select(reference => reference.Source))
                    .Concat(clip.Stages.SelectMany(stage =>
                        stage.RequireStockHostVideoPayload(
                                MiniMaxArchitectureModule.ArchitectureId,
                                MiniMaxArchitectureModule.Instance.Descriptor.DisplayName)
                            .FrameReferences
                            .Where(reference => !reference.IsEndpoint)
                            .Select(reference => reference.RawSource)));
            })
            .Any(candidate => StringUtils.Equals(candidate, source));

    public IVideoGenerationSession CreateSession(
        ArchitectureTimelineSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HostVideoRootSources rootSources = new(
            generator.CurrentMedia?.Duplicate(),
            generator.CurrentVae?.Duplicate());
        HostVideoDecodedStageInput stageInput = new(
            generator,
            context.Plan.FramesPerSecond,
            MiniMaxArchitectureModule.Instance.Descriptor.DisplayName,
            preserveAttachedAudio: true);
        return new MiniMaxGenerationSession(
            generator,
            context.Plan,
            rootSources,
            context.AudioSources,
            context.Boundaries,
            _baseReference,
            _refinerReference,
            new VideoStageRunner(generator, context.Plan),
            stageInput,
            context.RootAdoption);
    }
}
