using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class Ltx2ArchitectureModule : IVideoArchitectureModule
{
    internal static ArchitectureId ArchitectureId { get; } = new("ltx2");

    /// <summary>
    /// The grid an authored frame count snaps to, which for LTX-2 is also the VAE's temporal
    /// downscale — so pixel↔latent index conversions divide by it. Matches
    /// <c>ltx_director_guide.py</c>'s <c>downscale_index_formula[0]</c>. The two meanings coincide
    /// only here: MiniMax's snap grid is 17 and says nothing about its VAE.
    /// </summary>
    internal const int FrameGrid = 8;
    internal static ModelProfileId ProfileId { get; } = new("ltx-2.3");

    /// <summary>
    /// LTX's pixel→latent temporal mapping: the first pixel frame owns a latent frame of its own and
    /// every further <see cref="FrameGrid"/> pixel frames add one. Mirrored in the Comfy node as
    /// <c>swarm_prompt_relay.prompt_relay.pixel_to_latent_frames</c>; the pair is pinned by
    /// <c>Tests/fixtures/latent-frame-cases.json</c>.
    /// </summary>
    internal static int LatentFrameCount(int pixelFrames) =>
        (Math.Max(1, pixelFrames) - 1) / FrameGrid + 1;

    internal static Ltx2ArchitectureModule Instance { get; } = new();

    internal static bool IsLtx23VideoModel(T2IModel model) =>
        Instance.TryResolveModel(model, out _);

    public VideoArchitectureDescriptor Descriptor { get; } = new(
        ArchitectureId,
        "LTX Video 2.3",
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
        ArchitectureFeature.PromptRelay
            | ArchitectureFeature.FrameReferences
            | ArchitectureFeature.Retake
            | ArchitectureFeature.AudioReuse
            | ArchitectureFeature.AudioBoundaryCarry
            | ArchitectureFeature.LatentUpscale
            | ArchitectureFeature.LatentModelUpscale
            | ArchitectureFeature.AudioDerivedDuration
            | ArchitectureFeature.ReferenceFraming
            | ArchitectureFeature.IcLora,
        Ltx2BoundaryPolicy.Instance)
    {
        ConsumesTimelineAudio = true,
        FrameGrid = FrameGrid,
        StageGuideReferences = new(
            StageGuideReferenceKind.Generated
                | StageGuideReferenceKind.Base
                | StageGuideReferenceKind.Refiner
                | StageGuideReferenceKind.PreviousStage
                | StageGuideReferenceKind.ExplicitStage
                | StageGuideReferenceKind.Base2Edit),
    };

    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        if (model?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID
            || !string.Equals(
                model.ModelClass.ID,
                "lightricks-ltx-video-2-3",
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
        ArchitectureClipCompileContext context) =>
        Ltx2ClipPlanCompiler.Compile(clip, context);
}
