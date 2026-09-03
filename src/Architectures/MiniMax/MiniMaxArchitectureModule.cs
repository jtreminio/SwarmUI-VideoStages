using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.CharacterRefine;
using VideoStages.Execution;
using VideoStages.Execution.StockHost;
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
    internal const double ReferenceFramesPerSecond = 24;

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
            [BoundaryJoinType.Continue] = RuleDecision.Conditional(
                "minimax.boundary.continue",
                "MiniMax H3 conditions the next generated clip on the previous clip's video and optional audio tail.",
                new BoundaryRuleConstraints(
                    FrameStep: FrameGrid,
                    MinFrames: FrameGridOrigin,
                    MaxFrames: 362,
                    DefaultFrames: 39,
                    ContinuityExtraFrames: 0,
                    TargetRequiresGeneratedEntry: false,
                    TargetRequiresStage: true,
                    TargetDisallowsInitialReference: false)
                {
                    ContinueMode = ContinueBoundaryMode.Reference,
                }),
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
            AudioSourceKind.ControlNet,
            AudioSourceKind.AceStepFun,
        ],
        [
            ArchitectureEntryMode.TextToVideo,
            ArchitectureEntryMode.ImageToVideo,
            ArchitectureEntryMode.InitVideo,
        ],
        ArchitectureFeature.FrameReferences
            | ArchitectureFeature.ClipReferences
            | ArchitectureFeature.AudioDerivedDuration
            | ArchitectureFeature.ReferenceFraming
            | ArchitectureFeature.AudioReuse
            | ArchitectureFeature.AudioBoundaryCarry
            | ArchitectureFeature.LatentUpscale
            | ArchitectureFeature.CharacterRefine
            | ArchitectureFeature.CharacterRefineAppearanceReferences
            | ArchitectureFeature.CharacterRefineIdentityReferences,
        BoundaryPolicy)
    {
        ConsumesTimelineAudio = true,
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
            [FrameReferencePosition.Any],
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
        AudioSourceKind audioKind = AudioSource.Parse(clip.AudioSource).Kind;
        if (context.EntryMode == ArchitectureEntryMode.InitVideo
            && AudioSourceKindPolicy.CanUseAudioDerivedLength(clip)
            && Descriptor.AudioSourceKinds.Contains(audioKind))
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Error,
                "minimax.init-video.audio-derived-duration-unsupported",
                $"Clip {clip.Id}: MiniMax H3 init-video refinement requires a fixed frame "
                    + "count and cannot derive it from audio.",
                clip.Id));
        }
        Dictionary<int, IArchitectureStagePayload> stages = [];
        IReadOnlyList<MiniMaxReferencePlan> references = MiniMaxClipReferences.Compile(
            clip,
            diagnostics,
            context.HasPreviousClipOutput);
        StageSpec[] keyframeStages =
            [.. activeStages.Where(stage => stage.CharacterRefine is null)];
        (NativeFrameReferencePlan first, NativeFrameReferencePlan last) =
            NativeFrameReferences.Compile(
                clip,
                keyframeStages,
                stageModels,
                Descriptor,
                diagnostics,
                "minimax",
                allowHostStageSources: true,
                // H3 conditions on keyframes through a node that takes the latent as an input, so
                // a first keyframe composes with source footage instead of competing for its slot.
                allowFirstFrameWithInitVideo: true);
        foreach (StageSpec stage in activeStages)
        {
            // Assignments are resolver-vetted; a missing key is a caller contract violation.
            ResolvedVideoModel resolved = stageModels[stage.ClipStageRawIndex];
            bool passthrough = StagePassthroughPolicy.IsPassthrough(stage, Descriptor);
            bool characterRefine = stage.CharacterRefine is not null && !passthrough;
            bool decodedInput = !passthrough
                && StagePassthroughPolicy.HasDecodedVideoBefore(
                    clip,
                    stage,
                    context.EntryMode,
                    Descriptor);
            if (decodedInput && !double.IsFinite(stage.Control))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "minimax.stage-control.invalid",
                    $"Clip {clip.Id}: stage {stage.Id} decoded-input control must be a finite "
                        + "number.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            if (decodedInput
                && !characterRefine
                && StageStartStepPolicy.PartialControlRoundsToZero(
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
            if (characterRefine && !decodedInput)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "minimax.character-refine.decoded-input-required",
                    $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita requires InitVideo or the "
                        + "previous stage's decoded output.",
                    clip.Id,
                    stage.Id,
                    stage.ClipStageRawIndex));
            }
            if (characterRefine
                && StageUpscalePlanCompiler.Compile(stage).Mode == StageUpscaleMode.Latent)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Error,
                    "minimax.character-refine.latent-upscale-unsupported",
                    $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita cannot use latent upscale. "
                        + "Use pixel or model upscale before tracking.",
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

            StockHostVideoStagePayload host = StockHostVideoStagePayload.Compile(
                    ArchitectureId,
                    clip,
                    stage,
                    resolved,
                    resolved.LoraTarget)
                with
                {
                    FrameReferences = stage.CharacterRefine is not null
                        ? []
                        : FrameRefPlanCompiler.Compile(clip, stage),
                };
            MiniMaxCharacterRefinePlan characterRefinePlan = characterRefine
                ? CompileCharacterRefine(clip, stage, diagnostics)
                : null;
            stages[stage.ClipStageRawIndex] = new MiniMaxStagePayload(host, characterRefinePlan);
        }

        return new(
            new MiniMaxClipPayload(
                clip.ReferenceFraming,
                clip.ReuseAudio && activeStages.Count >= 3,
                MiniMaxAttentionWindowGraph.NormalizeSeconds(
                    clip.H3AttentionWindowSeconds),
                clip.H3TextEncoder,
                first,
                last,
                references),
            stages,
            diagnostics.AsReadOnly());
    }

    private static MiniMaxCharacterRefinePlan CompileCharacterRefine(
        ClipSpec clip,
        StageSpec stage,
        ICollection<PlanDiagnostic> diagnostics)
    {
        CharacterRefineSpec settings = stage.CharacterRefine;
        if (settings is null)
        {
            return null;
        }
        string[] imageIds = (clip.References ?? [])
            .Where(reference => reference.Kind == ClipReferenceKind.Image)
            .Select(reference => reference.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        HashSet<string> selectedIds = new(settings.PictureReferenceIds, StringComparer.Ordinal);
        string[] orderedSelectedIds = imageIds.Where(selectedIds.Contains).ToArray();
        IReadOnlyList<MiniMaxReferencePlan> pictures = MiniMaxClipReferences.CompileSelected(
            clip,
            orderedSelectedIds,
            diagnostics);
        if (pictures.Count != selectedIds.Count)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Error,
                "minimax.character-refine.picture-reference-invalid",
                $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita pictureReferenceIds must "
                    + "name usable image references on this clip.",
                clip.Id,
                stage.Id,
                stage.ClipStageRawIndex));
        }
        MiniMaxReferencePlan identity = null;
        if (settings.VerifyIdentity)
        {
            foreach (string imageId in imageIds)
            {
                identity = pictures.FirstOrDefault(reference => reference.Id == imageId)
                    ?? MiniMaxClipReferences.CompileSelected(
                        clip,
                        [imageId],
                        diagnostics).FirstOrDefault();
                if (identity is not null)
                {
                    break;
                }
            }
        }
        if (settings.VerifyIdentity && identity is null)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Error,
                "minimax.character-refine.identity-reference-missing",
                $"Clip {clip.Id}: stage {stage.Id} Make Them Bonita Verify Identity requires "
                    + "a usable image reference on this clip.",
                clip.Id,
                stage.Id,
                stage.ClipStageRawIndex));
            return null;
        }
        return new(
            pictures,
            identity,
            CharacterRefineSettingsPlan.From(settings));
    }
}

internal sealed record MiniMaxClipPayload(
    ReferenceFramingMode ReferenceFraming,
    bool ReuseAudio,
    double AttentionWindowSeconds,
    MiniMaxTextEncoder TextEncoder,
    NativeFrameReferencePlan FirstFrameReference,
    NativeFrameReferencePlan LastFrameReference,
    IReadOnlyList<MiniMaxReferencePlan> References) :
    IArchitectureClipPayload, INativeFrameReferenceClipPayload
{
    public ArchitectureId ArchitectureId => MiniMaxArchitectureModule.ArchitectureId;

    public (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height)
    {
        foreach (StagePlan stage in stages ?? [])
        {
            StageUpscaleMode mode = stage.Core.Upscale.Mode;
            if (mode != StageUpscaleMode.Latent
                && (mode is not (StageUpscaleMode.Pixel or StageUpscaleMode.Model)
                    || stage.Input is not (
                        StageInputKind.InitVideo
                        or StageInputKind.PreviousStage)))
            {
                continue;
            }
            (width, height) = StageUpscaleGraph.ResolveTargetDimensions(
                width,
                height,
                stage.Core.Upscale.Factor);
        }
        return (width, height);
    }
}

internal static class MiniMaxClipPayloadExtensions
{
    internal static MiniMaxClipPayload RequireMiniMaxPayload(this ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.ArchitecturePayload is not MiniMaxClipPayload payload)
        {
            throw Invariant.Failure(
                $"Clip {clip.ClipId} has no MiniMax architecture payload.");
        }
        return payload;
    }
}
