using System.Collections.Immutable;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.HostVideo;

/// <summary>
/// Last-priority baseline for model classes whose stock SwarmUI video graph is known to work.
/// This table is intentionally proof-based: optimistic host video flags alone are not admission.
/// </summary>
internal sealed class HostVideoArchitectureModule : IVideoArchitectureModule
{
    private sealed record ProvenHostPath(
        string CompatibilityClassId,
        string ModelClassId,
        VideoModelEntryAbility EntryAbilities);

    internal static ArchitectureId ArchitectureId { get; } = new("host-video");

    internal static ModelProfileId ProfileId { get; } = new("host-video");

    private static readonly IReadOnlyList<ProvenHostPath> ProvenPaths =
    [
        Path(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatHunyuanVideo,
            "hunyuan-video",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatHunyuanVideo,
            "hunyuan-video-skyreels",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatHunyuanVideo,
            "hunyuan-video-skyreels-i2v",
            VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatHunyuanVideo,
            "hunyuan-video-i2v",
            VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatHunyuanVideo,
            "hunyuan-video-i2v-v2",
            VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatGenmoMochi,
            "genmo-mochi-1",
            VideoModelEntryAbility.TextToVideo),
        Path(
            T2IModelClassSorter.CompatKandinsky5VidLite,
            "kandinsky5-video-lite",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatKandinsky5VidPro,
            "kandinsky5-video-pro",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatCosmos,
            "nvidia-cosmos-1-7b-text2world",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatCosmos,
            "nvidia-cosmos-1-14b-text2world",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatCosmos,
            "nvidia-cosmos-1-7b-video2world",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatCosmos,
            "nvidia-cosmos-1-14b-video2world",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatLtxv,
            "lightricks-ltx-video",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
        Path(
            T2IModelClassSorter.CompatLtxv2,
            "lightricks-ltx-video-2",
            VideoModelEntryAbility.TextToVideo | VideoModelEntryAbility.ImageToVideo),
    ];

    internal static HostVideoArchitectureModule Instance { get; } = new();

    public ArchitectureResolutionTier ResolutionTier =>
        ArchitectureResolutionTier.Fallback;

    public VideoArchitectureDescriptor Descriptor { get; } = new(
        ArchitectureId,
        "Host Video",
        ProfileId,
        [AudioSourceKind.Disabled],
        [
            new(
                ProfileId,
                "Video",
                [
                    ArchitectureEntryMode.TextToVideo,
                    ArchitectureEntryMode.ImageToVideo,
                    ArchitectureEntryMode.SourceVideo,
                ],
                [])
        ],
        new(
            ArchitectureCapability.GeneratedEntry
                | ArchitectureCapability.SourcedEntry
                | ArchitectureCapability.MultiStage
                | ArchitectureCapability.DecodedOutput,
            ClipCapability.Prompts | ClipCapability.SourceVideo,
            StageCapability.ImageInput
                | StageCapability.VideoInput
                | StageCapability.PixelUpscale
                | StageCapability.Lora),
        CreateBoundaryPolicy())
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

        resolved = new(model.Name, ArchitectureId, ProfileId, Descriptor)
        {
            ModelClassId = modelClass.ID,
            CompatibilityClassId = compatibility.ID,
            EntryAbilities = path.EntryAbilities,
            HostFactsAuthoritative = true,
        };
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
        string compatibilityClassId = null;
        foreach ((int rawStageIndex, ResolvedVideoModel resolved) in stageModels
            .OrderBy(pair => pair.Key))
        {
            if (resolved?.ArchitectureId != ArchitectureId)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(resolved.ModelClassId)
                || string.IsNullOrWhiteSpace(resolved.CompatibilityClassId)
                || resolved.EntryAbilities == VideoModelEntryAbility.None)
            {
                diagnostics.Add(Error(
                    clip,
                    "host-video.model-facts.missing",
                    $"Authored stage {rawStageIndex} is missing the proven host model facts "
                        + "required by the generic video fallback.",
                    rawStageIndex: rawStageIndex));
                continue;
            }
            if (compatibilityClassId is null)
            {
                compatibilityClassId = resolved.CompatibilityClassId;
            }
            else if (!string.Equals(
                compatibilityClassId,
                resolved.CompatibilityClassId,
                StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    clip,
                    "host-video.clip-compatibility-class.mixed",
                    $"Authored stage {rawStageIndex} resolves to compatibility class "
                        + $"'{resolved.CompatibilityClassId}' after '{compatibilityClassId}'. "
                        + "Use a separate clip to change video model families.",
                    rawStageIndex: rawStageIndex));
            }
        }

        Dictionary<int, IArchitectureStagePayload> stages = [];
        foreach (StageSpec stage in clip.Stages ?? [])
        {
            if (!stageModels.TryGetValue(
                stage.ClipStageRawIndex,
                    out ResolvedVideoModel resolved)
                || resolved?.ArchitectureId != ArchitectureId
                || string.IsNullOrWhiteSpace(resolved.ModelClassId)
                || string.IsNullOrWhiteSpace(resolved.CompatibilityClassId)
                || resolved.EntryAbilities == VideoModelEntryAbility.None)
            {
                diagnostics.Add(Error(
                    clip,
                    "host-video.stage-model.unsupported",
                    $"Stage {stage.Id} does not resolve to a proven generic host-video model.",
                    stage.Id,
                    stage.ClipStageRawIndex));
            }

            bool decodedInput = clip.SourceVideo is not null
                || stage.ClipStageIndex > 0;
            ImmutableArray<NormalLoraPlan> loras = NormalLoraPlanCompiler.Compile(
                clip,
                stage,
                ResolveLoraTargetPolicy(resolved?.CompatibilityClassId));
            if (!double.IsFinite(stage.Control)
                || decodedInput && (stage.Control < 0 || stage.Control > 1)
                || !decodedInput && stage.Control != 1)
            {
                diagnostics.Add(Error(
                    clip,
                    "host-video.stage-control.invalid",
                    $"Stage {stage.Id} control must be 1 for a generated root or within [0, 1] "
                        + "for decoded input.",
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            if (decodedInput
                && stage.Control <= 0
                && !loras.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error(
                    clip,
                    "normal-lora-requires-sampling-stage",
                    "Normal LoRAs require a sampling stage and cannot run on a passthrough.",
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            if (decodedInput
                && stage.Control > 0
                && stage.Control < 1
                && StartStep(stage.Steps, stage.Control) == 0)
            {
                diagnostics.Add(Error(
                    clip,
                    "host-video.stage-control.quantized-zero",
                    $"Stage {stage.Id} control is partial but rounds to sampler start step 0.",
                    stage.Id,
                    stage.ClipStageRawIndex));
            }

            stages[stage.ClipStageRawIndex] = new HostVideoStagePayload(
                resolved?.ModelName ?? stage.Model,
                resolved?.ModelClassId ?? "",
                resolved?.CompatibilityClassId ?? "",
                stage.Control,
                stage.Steps,
                stage.CfgScale,
                stage.Sampler,
                stage.Scheduler,
                StageUpscalePlanCompiler.Compile(stage),
                loras);
        }

        return new(
            new HostVideoClipPayload(clip.Id, compatibilityClassId ?? ""),
            stages,
            diagnostics.AsReadOnly());
    }

    internal static int StartStep(int steps, double control) =>
        (int)Math.Floor(steps * (1 - control));

    internal static NormalLoraTargetPolicy ResolveLoraTargetPolicy(
        string compatibilityClassId) =>
        string.Equals(
            compatibilityClassId,
            T2IModelClassSorter.CompatHunyuanVideo1_5.ID,
            StringComparison.Ordinal)
        || string.Equals(
            compatibilityClassId,
            T2IModelClassSorter.CompatHunyuanVideo.ID,
            StringComparison.Ordinal)
        || string.Equals(
            compatibilityClassId,
            T2IModelClassSorter.CompatKandinsky5VidLite.ID,
            StringComparison.Ordinal)
        || string.Equals(
            compatibilityClassId,
            T2IModelClassSorter.CompatKandinsky5VidPro.ID,
            StringComparison.Ordinal)
            ? NormalLoraTargetPolicy.ModelOnly
            : NormalLoraTargetPolicy.ModelAndTextEncoder;

    private static ProvenHostPath Path(
        T2IModelCompatClass compatibility,
        string modelClassId,
        VideoModelEntryAbility entryAbilities) =>
        new(compatibility.ID, modelClassId, entryAbilities);

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

    private static IArchitectureBoundaryPolicy CreateBoundaryPolicy() =>
        new ArchitectureBoundaryPolicy(new Dictionary<
            BoundaryExecutionMode,
            ArchitectureBoundaryModePolicy>
        {
            [BoundaryExecutionMode.Cut] = Boundary(
                RuleSupport.Supported,
                "host-video.boundary.cut",
                "Decoded host videos can be joined with a hard cut."),
            [BoundaryExecutionMode.Continue] = Boundary(
                RuleSupport.Unsupported,
                "host-video.boundary.continue.unsupported",
                "The generic host-video fallback has no continuity path."),
            [BoundaryExecutionMode.Crossfade] = Boundary(
                RuleSupport.Unsupported,
                "host-video.boundary.crossfade.unsupported",
                "The generic host-video fallback has no transition path."),
        });

    private static ArchitectureBoundaryModePolicy Boundary(
        RuleSupport support,
        string code,
        string reason) =>
        new(
            support,
            code,
            reason,
            FrameStep: 1,
            MinFrames: 0,
            MaxFrames: 0,
            DefaultFrames: 0,
            ContinuityExtraFrames: 0,
            TargetRequiresGeneratedEntry: false,
            TargetRequiresStage: false,
            TargetDisallowsInitialReference: false);
}

internal sealed record HostVideoStagePayload(
    string Model,
    string ModelClassId,
    string CompatibilityClassId,
    double Control,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler,
    StageUpscalePlan Upscale,
    ImmutableArray<NormalLoraPlan> Loras) : IArchitectureStagePayload
{
    public ArchitectureId ArchitectureId =>
        HostVideoArchitectureModule.ArchitectureId;
}

internal sealed record HostVideoClipPayload(
    int ClipId,
    string CompatibilityClassId) :
    IArchitectureClipPayload,
    IArchitectureClipGeometryProjection
{
    public ArchitectureId ArchitectureId =>
        HostVideoArchitectureModule.ArchitectureId;

    public (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height)
    {
        foreach (StagePlan stage in stages ?? [])
        {
            StageUpscalePlan upscale = stage.RequireHostVideoPayload().Upscale;
            if (upscale?.Mode != StageUpscaleMode.Pixel
                || stage.Input is not (
                    StageInputKind.SourceVideo
                    or StageInputKind.PreviousStage))
            {
                continue;
            }
            (width, height) = DimensionSnap.Snap(
                width * upscale.Factor,
                height * upscale.Factor);
        }
        return (width, height);
    }
}

internal static class HostVideoPlanExtensions
{
    internal static HostVideoStagePayload RequireHostVideoPayload(this StagePlan stage) =>
        stage?.ArchitecturePayload as HostVideoStagePayload
        ?? throw new InvalidOperationException(
            $"Stage {stage?.StageId} has no generic host-video payload.");
}
