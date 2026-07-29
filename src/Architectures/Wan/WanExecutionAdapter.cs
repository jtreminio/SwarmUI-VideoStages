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
    /// Validates request-global host video parameters before any host graph phase runs. Supported
    /// swap settings are passed through to the host builder; the remaining settings still change
    /// the result enough that silently omitting them would be the wrong answer.
    /// </summary>
    public IReadOnlyList<PlanDiagnostic> PreflightRequest(
        ArchitectureRequestPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<PlanDiagnostic> diagnostics = [];
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
            if (!double.IsFinite(swapPercent) || swapPercent < 0 || swapPercent > 1)
            {
                diagnostics.Add(Refuse(
                    $"'Video Swap Percent' must be finite and between 0 and 1, but was "
                    + $"'{swapPercent}'."));
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
                    + "contains exactly one Wan 2.2 clip using the current image-to-video "
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
                "'Video2Video Creativity' is non-default, but current Wan clips enter from a "
                + "still-image root. Partial denoise requires a source-video entry, which this "
                + "Wan slice does not support."));
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
