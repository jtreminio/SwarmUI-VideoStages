using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Wan image-to-video models recognized from the host's compatibility and entry facts. Exact
/// legacy profiles remain runtime aliases for the two paths which already had special handling;
/// they are not the authority for ordinary Wan image-entry support.
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

    internal static ModelProfileId OrdinaryImageToVideoProfileId { get; } =
        new("wan-i2v");

    private static IReadOnlyList<RecognizedProfile> LegacyRecognizedProfiles { get; } =
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
        [AudioSourceKind.Disabled],
        [
            Profile(
                ImageToVideoProfileId,
                "Wan 2.2 Image2Video 14B",
                [ArchitectureEntryMode.ImageToVideo, ArchitectureEntryMode.SourceVideo]),
            Profile(
                Ti2v5bProfileId,
                "Wan 2.2 Text/Image2Video 5B",
                [
                    ArchitectureEntryMode.TextToVideo,
                    ArchitectureEntryMode.ImageToVideo,
                    ArchitectureEntryMode.SourceVideo,
                ]),
            Profile(
                OrdinaryImageToVideoProfileId,
                "Wan Image2Video",
                [ArchitectureEntryMode.ImageToVideo, ArchitectureEntryMode.SourceVideo]),
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
                | StageCapability.Lora,
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
        if (!IsOrdinaryWanImageModel(model))
        {
            resolved = null;
            return false;
        }
        RecognizedProfile legacyMatch = LegacyRecognizedProfiles.SingleOrDefault(candidate =>
            string.Equals(
                modelClassId,
                candidate.ModelClassId,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                compatClassId,
                candidate.CompatClassId,
                StringComparison.Ordinal));
        ModelProfileId profileId =
            legacyMatch?.ProfileId ?? OrdinaryImageToVideoProfileId;
        VideoModelEntryAbility entryAbilities =
            profileId == Ti2v5bProfileId
                ? VideoModelEntryAbility.TextToVideo
                    | VideoModelEntryAbility.ImageToVideo
                : VideoModelEntryAbility.ImageToVideo;
        resolved = new(
            model.Name,
            ArchitectureId,
            profileId,
            Descriptor)
        {
            ModelClassId = modelClassId,
            CompatibilityClassId = compatClassId,
            EntryAbilities = entryAbilities,
            HostFactsAuthoritative = true,
        };
        return true;
    }

    internal static bool IsSupportedProfile(ModelProfileId profileId) =>
        profileId == OrdinaryImageToVideoProfileId
        || LegacyRecognizedProfiles.Any(candidate => candidate.ProfileId == profileId);

    private static bool IsOrdinaryWanImageModel(T2IModel model)
    {
        T2IModelClass modelClass = model?.ModelClass;
        T2IModelCompatClass compatibility = modelClass?.CompatClass;
        string modelClassId = modelClass?.ID ?? "";
        string compatibilityClassId = compatibility?.ID ?? "";
        if (model is null
            || modelClass is null
            || compatibility is null
            || !compatibility.IsImage2Video
            || !compatibilityClassId.StartsWith("wan-", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(modelClassId)
            || !(modelClassId.StartsWith("wan-2_1-", StringComparison.OrdinalIgnoreCase)
                || modelClassId.StartsWith("wan-2_2-", StringComparison.OrdinalIgnoreCase))
            || modelClass.IsLora
            || HasClassToken(modelClassId, "lora")
            || HasClassToken(modelClassId, "vae")
            || modelClassId.Contains("vace", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.Equals(
                modelClassId,
                Ti2v5bModelClassId,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                compatibilityClassId,
                T2IModelClassSorter.CompatWan22_5b.ID,
                StringComparison.Ordinal))
        {
            return false;
        }
        if (string.Equals(
                modelClassId,
                ImageToVideoModelClassId,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                compatibilityClassId,
                T2IModelClassSorter.CompatWan21_14b.ID,
                StringComparison.Ordinal))
        {
            return false;
        }
        // The compatibility family advertises both entry abilities broadly. A concrete T2V class
        // is still not an image-entry model.
        return !modelClassId.Contains("text2video", StringComparison.OrdinalIgnoreCase)
            || modelClassId.Contains("image2video", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasClassToken(string modelClassId, string token)
    {
        for (int index = 0; index <= modelClassId.Length - token.Length; index++)
        {
            bool hasLeadingBoundary =
                index == 0 || !char.IsLetterOrDigit(modelClassId[index - 1]);
            int followingIndex = index + token.Length;
            bool hasTrailingBoundary =
                followingIndex == modelClassId.Length
                || !char.IsLetterOrDigit(modelClassId[followingIndex]);
            if (
                hasLeadingBoundary
                && hasTrailingBoundary
                && modelClassId
                    .AsSpan(index, token.Length)
                    .Equals(token.AsSpan(), StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }
        return false;
    }

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
        string displayName,
        IReadOnlyList<ArchitectureEntryMode> entryModes) =>
        new(
            id,
            displayName,
            entryModes,
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
