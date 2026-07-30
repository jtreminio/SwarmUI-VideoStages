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
    /// Validates request-global host video parameters before any host graph phase runs. Legacy
    /// request-global swap settings are projected to one warning and isolated by the host handler;
    /// the remaining settings still change the result enough that silently omitting them would be
    /// the wrong answer.
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
            ClipPlan onlyClip = context.Plan.Clips.Count == 1
                ? context.Plan.Clips[0]
                : null;
            bool hasValidRuntimeContract = false;
            if (onlyClip is not null)
            {
                try
                {
                    WanRuntimeClipContract.Validate(context.Plan, onlyClip);
                    hasValidRuntimeContract = true;
                }
                catch (InvalidOperationException)
                {
                    // The request-global option reports malformed immutable plans through the
                    // same user-facing refusal as all other ineligible timeline shapes.
                }
            }
            StagePlan[] generatingStages = onlyClip?.Stages
                .Where(stage => !stage.IsPassthrough)
                .ToArray() ?? [];
            bool isPureGenerated14b =
                hasValidRuntimeContract
                && onlyClip.EntryMode == ArchitectureEntryMode.ImageToVideo
                && onlyClip.ArchitecturePayload is WanClipPayload
                {
                    ProfileId: var clipProfile,
                }
                && clipProfile == WanArchitectureModule.ImageToVideoProfileId;
            bool hasExactlyOneTerminalOwner =
                hasValidRuntimeContract
                && generatingStages.Length > 0
                && onlyClip.Stages.Count(
                    stage => stage.ArchitecturePayload is WanStagePayload
                    {
                        OwnsVideoEndFrame: true,
                    }) == 1
                && generatingStages[^1].ArchitecturePayload is WanStagePayload
                {
                    OwnsVideoEndFrame: true,
                };
            bool isSingleCurrentWan =
                isPureGenerated14b
                && hasExactlyOneTerminalOwner;
            if (!isSingleCurrentWan)
            {
                string families = string.Join(
                    ", ",
                    context.Plan.Clips
                        .Select(clip => clip.Architecture?.Id.ToString() ?? "<unresolved>")
                        .Distinct());
                diagnostics.Add(Refuse(
                    "'Video End Frame' is request-global and is ambiguous unless the timeline "
                    + "contains exactly one generated Wan 2.2 ImageToVideo clip with at least "
                    + "one generating stage, canonical 14B ownership, and the current image-to-video "
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
