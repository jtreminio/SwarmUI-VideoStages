using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Exact Wan 2.2 profiles proven through the host image-to-video builder. Clips enter from either
/// the generated host image or bounded clip-local source footage, then may chain one profile over
/// the preceding decoded stage output. Every other authoring feature is declared unsupported so
/// common capability validation rejects it before graph mutation rather than an executor
/// discovering it late.
/// </summary>
internal sealed class WanArchitectureModule : IVideoArchitectureModule
{
    private sealed record RecognizedProfile(
        string ModelClassId,
        string CompatClassId,
        ModelProfileId ProfileId);

    internal static ArchitectureId ArchitectureId { get; } = new("wan22");

    /// <summary>
    /// Wan's VAE compresses four pixel frames into one latent frame, so authored durations and
    /// boundary windows step on four. Published as the profile's frame grid.
    /// </summary>
    internal const int FrameGrid = 4;

    /// <summary>
    /// The exact 14B host model class recognized by the original profile.
    /// </summary>
    internal const string ImageToVideoModelClassId = "wan-2_2-image2video-14b";

    /// <summary>The exact 5B class whose image-conditioned path is proven by this module.</summary>
    internal const string Ti2v5bModelClassId = "wan-2_2-ti2v-5b";

    internal static ModelProfileId ImageToVideoProfileId { get; } = new("wan-2.2-i2v-14b");

    internal static ModelProfileId Ti2v5bProfileId { get; } = new("wan-2.2-ti2v-5b");

    private static IReadOnlyList<RecognizedProfile> RecognizedProfiles { get; } =
    [
        new(
            ImageToVideoModelClassId,
            T2IModelClassSorter.CompatWan21_14b.ID,
            ImageToVideoProfileId),
        new(
            Ti2v5bModelClassId,
            T2IModelClassSorter.CompatWan22_5b.ID,
            Ti2v5bProfileId),
    ];

    internal const string NormalLoraRequiresSamplingStageCode =
        "normal-lora-requires-sampling-stage";

    internal const string NormalLoraRequiresSamplingStageReason =
        "Normal LoRAs require a sampling stage and cannot have nonzero weight on a samplerless passthrough.";

    internal static RuleDecision NormalLoraRequiresSamplingStageRule { get; } =
        RuleDecision.Conditional(
            NormalLoraRequiresSamplingStageCode,
            NormalLoraRequiresSamplingStageReason,
            RuleScope.Stage,
            new MinimumStageControlRuleConstraints(0));

    internal static WanArchitectureModule Instance { get; } = new();

    public VideoArchitectureDescriptor Descriptor { get; } = new(
        ArchitectureId,
        "Wan 2.2",
        ImageToVideoProfileId,
        [ArchitectureEntryMode.ImageToVideo, ArchitectureEntryMode.SourceVideo],
        [AudioSourceKind.Disabled],
        [
            Profile(ImageToVideoProfileId, "Wan 2.2 Image2Video 14B"),
            Profile(Ti2v5bProfileId, "Wan 2.2 Text/Image2Video 5B"),
        ],
        new(
            ArchitectureCapability.GeneratedEntry
                | ArchitectureCapability.SourcedEntry
                | ArchitectureCapability.MultiStage
                | ArchitectureCapability.DecodedOutput,
            ClipCapability.Prompts | ClipCapability.SourceVideo,
            StageCapability.ImageInput | StageCapability.VideoInput | StageCapability.Lora,
            OutputCapability.Video),
        WanBoundaryPolicy.Instance)
    {
        StageGuideReferences = new(
            StageGuideReferenceKind.Generated | StageGuideReferenceKind.PreviousStage),
    };

    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        string modelClassId = model?.ModelClass?.ID;
        string compatClassId = model?.ModelClass?.CompatClass?.ID;
        RecognizedProfile match = RecognizedProfiles.SingleOrDefault(candidate =>
            string.Equals(
                modelClassId,
                candidate.ModelClassId,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                compatClassId,
                candidate.CompatClassId,
                StringComparison.Ordinal));
        if (match is null)
        {
            resolved = null;
            return false;
        }
        resolved = new(
            model.Name,
            ArchitectureId,
            match.ProfileId,
            Descriptor);
        return true;
    }

    internal static bool IsSupportedProfile(ModelProfileId profileId) =>
        RecognizedProfiles.Any(candidate => candidate.ProfileId == profileId);

    public ArchitectureClipCompilation ValidateAndCompileClip(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ArchitectureClipCompileContext context)
    {
        WanClipPlanCompilation compilation = WanClipPlanCompiler.Compile(
            clip,
            stageModels,
            context);
        return new(
            compilation.Payload,
            compilation.Stages.ToDictionary(
                pair => pair.Key,
                pair => (IArchitectureStagePayload)pair.Value),
            compilation.Diagnostics);
    }

    private static VideoModelProfileDescriptor Profile(
        ModelProfileId id,
        string displayName) =>
        new(
            id,
            displayName,
            ModelProfileCapability.SamplerSelection
                | ModelProfileCapability.SchedulerSelection
                | ModelProfileCapability.DimensionRules
                | ModelProfileCapability.FrameRules
                | ModelProfileCapability.NormalLora,
            [NormalLoraRequiresSamplingStageRule])
        {
            FrameGrid = FrameGrid,
        };
}
