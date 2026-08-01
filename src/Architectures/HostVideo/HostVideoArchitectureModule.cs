using System.Collections.Immutable;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo;
using VideoStages.Planning;

namespace VideoStages.Architectures.HostVideo;

/// <summary>
/// Last-priority baseline for model classes whose stock SwarmUI video graph is known to work.
/// This table is intentionally proof-based: optimistic host video flags alone are not admission.
/// </summary>
internal sealed class HostVideoArchitectureModule :
    IVideoArchitectureModule,
    IArchitectureEffectiveRequestProjector
{
    internal sealed record ProvenHostPath(
        string CompatibilityClassId,
        string ModelClassId);

    internal static ArchitectureId ArchitectureId { get; } = new("host-video");

    internal static ModelProfileId ProfileId { get; } = new("host-video");

    internal static IReadOnlyList<ProvenHostPath> ProvenPaths { get; } =
    [
        Path(T2IModelClassSorter.CompatHunyuanVideo1_5, "hunyuan-video-1_5"),
        Path(T2IModelClassSorter.CompatHunyuanVideo, "hunyuan-video"),
        Path(T2IModelClassSorter.CompatHunyuanVideo, "hunyuan-video-skyreels"),
        Path(T2IModelClassSorter.CompatHunyuanVideo, "hunyuan-video-skyreels-i2v"),
        Path(T2IModelClassSorter.CompatHunyuanVideo, "hunyuan-video-i2v"),
        Path(T2IModelClassSorter.CompatHunyuanVideo, "hunyuan-video-i2v-v2"),
        Path(T2IModelClassSorter.CompatGenmoMochi, "genmo-mochi-1"),
        Path(T2IModelClassSorter.CompatKandinsky5VidLite, "kandinsky5-video-lite"),
        Path(T2IModelClassSorter.CompatKandinsky5VidPro, "kandinsky5-video-pro"),
        Path(T2IModelClassSorter.CompatCosmos, "nvidia-cosmos-1-7b-text2world"),
        Path(T2IModelClassSorter.CompatCosmos, "nvidia-cosmos-1-14b-text2world"),
        Path(T2IModelClassSorter.CompatCosmos, "nvidia-cosmos-1-7b-video2world"),
        Path(T2IModelClassSorter.CompatCosmos, "nvidia-cosmos-1-14b-video2world"),
        Path(T2IModelClassSorter.CompatLtxv, "lightricks-ltx-video"),
        Path(T2IModelClassSorter.CompatLtxv2, "lightricks-ltx-video-2"),
    ];

    internal static HostVideoArchitectureModule Instance { get; } = new();

    public bool IsFallback => true;

    public VideoArchitectureDescriptor Descriptor { get; } = new(
        ArchitectureId,
        "Host Video",
        [AudioSourceKind.Disabled],
        [
            ArchitectureEntryMode.TextToVideo,
            ArchitectureEntryMode.ImageToVideo,
            ArchitectureEntryMode.InitVideo,
        ],
        ArchitectureFeature.None,
        ArchitectureBoundaryPolicy.CutOnly(
            "host-video",
            "Decoded host videos can be joined with a hard cut."))
    {
        // Unknown video families do not share one trustworthy temporal grid.
        FrameGrid = 1,
        StageGuideReferences = new(
            StageGuideReferenceKind.Generated | StageGuideReferenceKind.PreviousStage),
    };

    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        T2IModelClass modelClass = model?.ModelClass;
        T2IModelCompatClass compatibility = modelClass?.CompatClass;
        ProvenHostPath path = ProvenPaths.SingleOrDefault(candidate =>
            string.Equals(
                candidate.CompatibilityClassId,
                compatibility?.ID,
                StringComparison.Ordinal)
            && string.Equals(
                candidate.ModelClassId,
                modelClass?.ID,
                StringComparison.OrdinalIgnoreCase));
        if (model is null
            || modelClass is null
            || compatibility is null
            || path is null
            || modelClass.IsLora)
        {
            resolved = null;
            return false;
        }

        resolved = new(
            model.Name,
            ProfileId,
            Descriptor,
            modelClass.ID,
            compatibility.ID,
            [],
            compatibility.LorasTargetTextEnc);
        return true;
    }

    public ArchitectureEffectiveRequestProjection ProjectEffectiveRequest(
        ArchitectureEffectiveRequestProjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        EffectiveRequestDecision[] requestDecisions =
            context.LegacyVideoSwap?.IsConfigured == true
                && context.AuthoredRootTimelineIndex.HasValue
                && context.OwnedClips.Any(
                    owned =>
                        owned.TimelineIndex
                        == context.AuthoredRootTimelineIndex.Value)
            ?
            [
                EffectiveRequestDecision.Ignore(
                    "effective-request.host-video-swap-ignored",
                    "Generic VideoStages ignores SwarmUI's request-global Video Swap Model, "
                        + "Video Swap Percent, and Video Swap section settings. The authored "
                        + "values remain in request metadata. Create separate timeline stages "
                        + "instead."),
            ]
            : [];
        return new(
            Array.Empty<ArchitectureProjectedEffectiveClip>(),
            Array.AsReadOnly(requestDecisions));
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
        // Model facts are registry-owned and clip compatibility is resolver-owned. This compiler
        // consumes that vetted assignment; an absent key is a caller contract violation, not
        // another user-facing validation result.
        string compatibilityClassId = activeStages.Count == 0
            ? ""
            : stageModels[activeStages[0].ClipStageRawIndex].CompatibilityClassId;
        Dictionary<int, IArchitectureStagePayload> stages = [];
        foreach (StageSpec stage in activeStages)
        {
            ResolvedVideoModel resolved = stageModels[stage.ClipStageRawIndex];
            bool decodedInput = clip.InitVideo is not null
                || stage.ClipStageIndex > 0;
            NormalLoraTargetPolicy loraTargetPolicy =
                resolved.LorasTargetTextEncoder == false
                    ? NormalLoraTargetPolicy.ModelOnly
                    : NormalLoraTargetPolicy.ModelAndTextEncoder;
            ImmutableArray<NormalLoraPlan> loras = NormalLoraPlanCompiler.Compile(
                clip,
                stage,
                loraTargetPolicy);
            if (decodedInput
                && (!double.IsFinite(stage.Control)
                    || stage.Control < 0
                    || stage.Control > 1))
            {
                diagnostics.Add(Error(
                    clip,
                    "host-video.stage-control.invalid",
                    $"Stage {stage.Id} decoded-input control must be finite and within [0, 1].",
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            if (decodedInput
                && stage.Control > 0
                && HostVideoStageSchedulePolicy.IsQuantizedZeroPartial(
                    stage.Steps,
                    stage.Control))
            {
                diagnostics.Add(Error(
                    clip,
                    "host-video.stage-control.quantized-zero",
                    $"Stage {stage.Id} control is partial but rounds to sampler start step 0.",
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
                    loras));
        }

        return new(
            new HostVideoClipPayload(clip.Id, compatibilityClassId),
            stages,
            diagnostics.AsReadOnly());
    }

    private static ProvenHostPath Path(
        T2IModelCompatClass compatibility,
        string modelClassId) =>
        new(compatibility.ID, modelClassId);

    private static PlanDiagnostic Error(
        ClipSpec clip,
        string code,
        string message,
        int? stageId = null,
        int? rawStageIndex = null) =>
        new(
            PlanDiagnosticSeverity.Error,
            code,
            $"Clip {clip.Id}: {message}",
            clip.Id,
            stageId,
            rawStageIndex);
}

internal sealed record HostVideoClipPayload(
    int ClipId,
    string CompatibilityClassId) :
    IArchitectureClipPayload
{
    public ArchitectureId ArchitectureId =>
        HostVideoArchitectureModule.ArchitectureId;

    public (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height) =>
        HostVideoStageGeometry.ProjectFinalDimensions(stages, width, height);
}

internal static class HostVideoPlanExtensions
{
    internal static StockHostVideoStagePayload RequireHostVideoPayload(this StagePlan stage)
    {
        if (stage?.ArchitecturePayload is not StockHostVideoStagePayload payload
            || payload.ArchitectureId != HostVideoArchitectureModule.ArchitectureId)
        {
            throw new InvalidOperationException(
                $"Stage {stage?.StageId} has no generic host-video payload.");
        }
        return payload;
    }
}
