using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class Ltx2ArchitectureModule : IVideoArchitectureModule
{
    private sealed record RecognizedProfile(
        string ModelClassId,
        ModelProfileId ProfileId);

    internal static ArchitectureId ArchitectureId { get; } = new("ltx2");

    /// <summary>
    /// LTX-2's frame-count grid and VAE temporal downscale.
    /// </summary>
    internal const int FrameGrid = 8;

    internal static ModelProfileId Ltx23ProfileId { get; } = new("ltx-2.3");

    internal static ModelProfileId Ltx25ProfileId { get; } = new("ltx-2.5");

    private static IReadOnlyList<RecognizedProfile> ExactProfiles { get; } =
    [
        new("lightricks-ltx-video-2-3", Ltx23ProfileId),
        new("lightricks-ltx-video-2-5", Ltx25ProfileId),
    ];

    /// <summary>
    /// LTX's pixel→latent temporal mapping: the first pixel frame owns a latent frame of its own and
    /// every further <see cref="FrameGrid"/> pixel frames add one. Mirrored in the Comfy node as
    /// <c>swarm_prompt_relay.prompt_relay.pixel_to_latent_frames</c>; the pair is pinned by
    /// <c>Tests/fixtures/latent-frame-cases.json</c>.
    /// </summary>
    internal static int LatentFrameCount(int pixelFrames) =>
        (Math.Max(1, pixelFrames) - 1) / FrameGrid + 1;

    internal static Ltx2ArchitectureModule Instance { get; } = new();

    internal static bool IsSupportedVideoModel(T2IModel model) =>
        Instance.TryResolveModel(model, out _);

    public VideoArchitectureDescriptor Descriptor { get; } = new(
        ArchitectureId,
        "LTX Video 2",
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
            | ArchitectureFeature.StageReferenceStrengths
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
        string modelClassId = model?.ModelClass?.ID;
        RecognizedProfile profile = ExactProfiles.SingleOrDefault(candidate =>
            string.Equals(
                modelClassId,
                candidate.ModelClassId,
                StringComparison.OrdinalIgnoreCase));
        if (model?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID
            || profile is null)
        {
            resolved = null;
            return false;
        }
        resolved = new(
            model.Name,
            profile.ProfileId,
            Descriptor,
            modelClassId,
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
