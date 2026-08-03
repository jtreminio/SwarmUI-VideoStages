using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo;
using VideoStages.Planning;

namespace VideoStages.Architectures.MiniMax;

/// <summary>MiniMax H3: one sampling pass over a joint audio-video latent.</summary>
internal sealed class MiniMaxArchitectureModule : IVideoArchitectureModule
{
    internal static ArchitectureId ArchitectureId { get; } = new("minimax");

    internal static ModelProfileId ProfileId { get; } = new("minimax-h3");

    /// <summary>
    /// H3 samples 17-frame blocks after an opening five-frame block, so valid generated counts
    /// are 5, 22, 39, ... Mirrors core's <c>WorkflowGenerator.MiniMaxH3AlignFrames</c>.
    /// </summary>
    internal const int FrameGrid = 17;

    internal const int FrameGridOrigin = 5;

    /// <summary>The checkpoint class; core registers its video VAE under the same compat class.</summary>
    internal const string ModelClassId = "minimax-h3";

    /// <summary>H3 is guidance-distilled; core's own path defaults it to a CFG of one.</summary>
    internal const double UnguidedCfgScale = 1;

    private static ArchitectureBoundaryPolicy BoundaryPolicy { get; } =
        new(new Dictionary<BoundaryJoinType, RuleDecision>
        {
            [BoundaryJoinType.Cut] = RuleDecision.Supported(
                "minimax.boundary.cut",
                "Decoded MiniMax H3 clips can be joined with a hard cut."),
            [BoundaryJoinType.Continue] = RuleDecision.Unsupported(
                "minimax.boundary.continue.unsupported",
                "MiniMax H3 has no N-frame latent-tail continuation path."),
            [BoundaryJoinType.Crossfade] = RuleDecision.Conditional(
                "minimax.boundary.crossfade",
                "Decoded MiniMax H3 clips can be crossfaded.",
                new BoundaryRuleConstraints(
                    FrameStep: 1,
                    MinFrames: 1,
                    MaxFrames: 48,
                    DefaultFrames: 8,
                    ContinuityExtraFrames: 0,
                    TargetRequiresGeneratedEntry: false,
                    TargetRequiresStage: false,
                    TargetDisallowsInitialReference: false)),
        });

    internal static MiniMaxArchitectureModule Instance { get; } = new();

    public VideoArchitectureDescriptor Descriptor { get; } = new(
        ArchitectureId,
        "MiniMax H3",
        [
            AudioSourceKind.Native,
            AudioSourceKind.Upload,
            AudioSourceKind.AceStepFun,
        ],
        [
            ArchitectureEntryMode.TextToVideo,
            ArchitectureEntryMode.ImageToVideo,
        ],
        ArchitectureFeature.FrameReferences
            | ArchitectureFeature.AudioSegments
            | ArchitectureFeature.AudioDerivedDuration
            | ArchitectureFeature.ReferenceFraming
            | ArchitectureFeature.AudioReuse,
        BoundaryPolicy)
    {
        FrameGrid = FrameGrid,
        FrameGridOrigin = FrameGridOrigin,
        StageGuideReferences = new(
            StageGuideReferenceKind.Generated | StageGuideReferenceKind.PreviousStage),
    };

    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        if (model?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatMiniMaxH3.ID
            || !string.Equals(
                model.ModelClass.ID,
                ModelClassId,
                StringComparison.OrdinalIgnoreCase))
        {
            resolved = null;
            return false;
        }
        resolved = new(
            model.Name,
            ProfileId,
            Descriptor,
            model.ModelClass.ID,
            model.ModelClass.CompatClass.ID,
            ["first", "last"],
            model.ModelClass.CompatClass.LorasTargetTextEnc);
        return true;
    }

    public ArchitectureClipCompilation ValidateAndCompileClip(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ArchitectureClipCompileContext context)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stageModels);
        ArgumentNullException.ThrowIfNull(context);

        List<PlanDiagnostic> diagnostics = [];
        IReadOnlyList<StageSpec> activeStages = clip.Stages ?? [];
        Dictionary<int, IArchitectureStagePayload> stages = [];
        (NativeFrameReferencePlan first, NativeFrameReferencePlan last) =
            NativeFrameReferences.Compile(
                clip,
                activeStages,
                stageModels,
                Descriptor,
                diagnostics,
                "minimax",
                "MiniMax H3",
                allowHostStageSources: true);
        foreach (StageSpec stage in activeStages)
        {
            // Assignments are resolver-vetted; a missing key is a caller contract violation.
            ResolvedVideoModel resolved = stageModels[stage.ClipStageRawIndex];
            NormalLoraTargetPolicy loraTargetPolicy =
                resolved.LorasTargetTextEncoder == false
                    ? NormalLoraTargetPolicy.ModelOnly
                    : NormalLoraTargetPolicy.ModelAndTextEncoder;
            bool passthrough = ArchitectureStageActivity.IsPassthrough(stage, Descriptor);
            if (stage.ClipStageIndex > 0
                && !passthrough
                && HostVideoStageSchedulePolicy.IsQuantizedZeroPartial(
                    stage.Steps,
                    stage.Control))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "minimax.stage-control.quantized-zero",
                    $"Clip {clip.Id}: stage {stage.Id} control is partial but rounds to sampler "
                        + "start step 0.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            if (stage.CfgScale != UnguidedCfgScale && !passthrough)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "minimax.stage.cfg-scale.non-unity",
                    $"Clip {clip.Id}: stage {stage.Id} uses CFG {stage.CfgScale}. MiniMax H3 is "
                        + $"guidance-distilled and expects CFG {UnguidedCfgScale}; other values "
                        + "usually degrade the result.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }

            stages[stage.ClipStageRawIndex] = new StockHostVideoStagePayload(
                ArchitectureId,
                resolved.ModelClassId,
                resolved.CompatibilityClassId,
                loraTargetPolicy,
                new StageCorePlan(
                    stage.Control,
                    stage.Steps,
                    stage.CfgScale,
                    stage.Sampler,
                    stage.Scheduler,
                    StageUpscalePlanCompiler.Compile(stage),
                    NormalLoraPlanCompiler.Compile(clip, stage, loraTargetPolicy)));
        }

        return new(
            new MiniMaxClipPayload(
                clip.Id,
                clip.ReferenceFraming,
                clip.ReuseAudio && activeStages.Count >= 3,
                first,
                last),
            stages,
            diagnostics.AsReadOnly());
    }
}

internal sealed record MiniMaxClipPayload(
    int ClipId,
    ReferenceFramingMode ReferenceFraming,
    bool ReuseAudio,
    NativeFrameReferencePlan FirstFrameReference,
    NativeFrameReferencePlan LastFrameReference) :
    IArchitectureClipPayload
{
    public ArchitectureId ArchitectureId => MiniMaxArchitectureModule.ArchitectureId;

    public (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height) =>
        HostVideoStageGeometry.ProjectFinalDimensions(stages, width, height);
}

internal static class MiniMaxClipPayloadExtensions
{
    internal static MiniMaxClipPayload RequireMiniMaxPayload(this ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.ArchitecturePayload is not MiniMaxClipPayload payload)
        {
            throw VideoStagesInvariant.Failure(
                $"Clip {clip.ClipId} has no MiniMax architecture payload.");
        }
        return payload;
    }
}
