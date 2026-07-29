using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
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
    /// Validates request-global host video parameters before any host graph phase runs. Supported
    /// swap settings are passed through to the host builder; the remaining settings still change
    /// the result enough that silently omitting them would be the wrong answer.
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
        if (generator.UserInput.Get(T2IParamTypes.VideoSwapModel, null) is T2IModel swapModel)
        {
            if (!WanArchitectureModule.Instance.TryResolveModel(swapModel, out ResolvedVideoModel swap))
            {
                diagnostics.Add(Refuse(
                    $"'Video Swap Model' '{swapModel.Name}' is not a supported Wan 2.2 "
                    + "image-to-video model."));
            }
            else
            {
                foreach (ClipPlan clip in context.Plan.Clips.Where(
                    clip => clip.Architecture.Id == ArchitectureId))
                {
                    foreach (StagePlan stage in clip.Stages.Where(stage => !stage.IsPassthrough))
                    {
                        string mismatch = DescribeSwapIncompatibility(
                            swap,
                            clip.ClipId,
                            stage.StageId,
                            stage.ResolvedModel);
                        if (mismatch is null)
                        {
                            continue;
                        }
                        diagnostics.Add(Refuse(
                            mismatch,
                            clip.ClipId,
                            stage.StageId));
                    }
                }
            }
            double swapPercent = generator.UserInput.Get(T2IParamTypes.VideoSwapPercent, 0.5);
            bool validSwapPercent =
                double.IsFinite(swapPercent)
                && swapPercent >= 0
                && swapPercent <= 1;
            if (!validSwapPercent)
            {
                diagnostics.Add(Refuse(
                    $"'Video Swap Percent' must be finite and between 0 and 1, but was "
                    + $"'{swapPercent}'."));
            }
            else
            {
                foreach (ClipPlan clip in context.Plan.Clips.Where(
                    clip => clip.Architecture.Id == ArchitectureId))
                {
                    foreach (StagePlan stage in clip.Stages.Where(
                        stage => !stage.IsPassthrough
                            && stage.Input is StageInputKind.SourceVideo
                                or StageInputKind.PreviousStage))
                    {
                        WanStagePayload payload = stage.RequireWanPayload();
                        if (payload.Control >= 1
                            || WanStageSchedulePolicy.HasSwapHighNoiseWindow(
                                payload.Steps,
                                payload.Control,
                                swapPercent))
                        {
                            continue;
                        }
                        int startStep = WanStageSchedulePolicy.StartStep(
                            payload.Steps,
                            payload.Control);
                        int highEndStep = WanStageSchedulePolicy.HostHighEndStep(
                            payload.Steps,
                            swapPercent);
                        diagnostics.Add(Refuse(
                            $"'Video Swap Model' gives clip {clip.ClipId} stage {stage.StageId} "
                                + $"no high-noise sampling window: the partial-control start step "
                                + $"{startStep} must be less than the swap high-noise end step "
                                + $"{highEndStep}.",
                            clip.ClipId,
                            stage.StageId));
                    }
                }
            }
        }
        if (generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null) is not null)
        {
            ClipPlan onlyClip = context.Plan.Clips.Count == 1
                ? context.Plan.Clips[0]
                : null;
            StagePlan[] activeStages = onlyClip?.Stages
                .Where(stage => !stage.IsPassthrough)
                .ToArray() ?? [];
            bool isSingleCurrentWan =
                onlyClip?.Architecture?.Id == ArchitectureId
                && onlyClip.EntryMode == ArchitectureEntryMode.ImageToVideo
                && activeStages.Length == 1
                && activeStages[0].ResolvedModel?.ArchitectureId == ArchitectureId
                && activeStages[0].ResolvedModel?.ModelProfileId
                    == WanArchitectureModule.ImageToVideoProfileId;
            if (!isSingleCurrentWan)
            {
                string families = string.Join(
                    ", ",
                    context.Plan.Clips
                        .Select(clip => clip.Architecture?.Id.ToString() ?? "<unresolved>")
                        .Distinct());
                diagnostics.Add(Refuse(
                    "'Video End Frame' is request-global and is ambiguous unless the timeline "
                    + "contains exactly one Wan 2.2 ImageToVideo clip using the current "
                    + "image-to-video "
                    + $"profile. This request has {context.Plan.Clips.Count} clip(s) across "
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
        WanRootMediaHandoff handoff = new(generator);
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

    internal static bool IsSwapCompatible(
        ResolvedVideoModel swap,
        ResolvedVideoModel stage) =>
        swap is not null
        && stage is not null
        && swap.ArchitectureId == stage.ArchitectureId
        && swap.ModelProfileId == stage.ModelProfileId;

    internal static string DescribeSwapIncompatibility(
        ResolvedVideoModel swap,
        int clipId,
        int stageId,
        ResolvedVideoModel stage) =>
        IsSwapCompatible(swap, stage)
            ? null
            : $"'Video Swap Model' '{swap?.ModelName ?? "<unresolved>"}' resolves to architecture "
                + $"'{swap?.ArchitectureId.ToString() ?? "<unresolved>"}' profile "
                + $"'{swap?.ModelProfileId.ToString() ?? "<unresolved>"}', but clip {clipId} stage "
                + $"{stageId} uses model '{stage?.ModelName ?? "<unresolved>"}' architecture "
                + $"'{stage?.ArchitectureId.ToString() ?? "<unresolved>"}' profile "
                + $"'{stage?.ModelProfileId.ToString() ?? "<unresolved>"}'.";

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
