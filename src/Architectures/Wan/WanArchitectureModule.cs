using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.HostVideo;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// WAN models recognized from the host's compatibility and checkpoint facts. Exact legacy
/// profiles remain aliases for established paths; entry authorization is family-wide.
/// </summary>
internal sealed class WanArchitectureModule :
    IVideoArchitectureModule,
    IArchitectureEffectiveRequestProjector
{
    private sealed record RecognizedProfile(
        string ModelClassId,
        string CompatClassId,
        ModelProfileId ProfileId);

    internal static ArchitectureId ArchitectureId { get; } = new("wan22");

    /// <summary>
    /// Wan's VAE compresses four pixel frames into one latent frame, so authored durations and
    /// boundary windows step on four. Published as an architecture runtime fact.
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

    internal static WanArchitectureModule Instance { get; } = new();

    public VideoArchitectureDescriptor Descriptor { get; } = new(
        ArchitectureId,
        "WAN Video",
        [AudioSourceKind.Disabled],
        [
            ArchitectureEntryMode.TextToVideo,
            ArchitectureEntryMode.ImageToVideo,
            ArchitectureEntryMode.SourceVideo,
        ],
        new(
            ArchitectureCapability.GeneratedEntry
                | ArchitectureCapability.SourcedEntry
                | ArchitectureCapability.MultiStage
                | ArchitectureCapability.DecodedOutput,
            ClipCapability.Prompts
                | ClipCapability.SourceVideo
                | ClipCapability.References,
            StageCapability.ImageInput
                | StageCapability.VideoInput
                | StageCapability.PixelUpscale
                | StageCapability.Lora
                | StageCapability.FrameReferences),
        ArchitectureBoundaryPolicy.CutOnly(
            "wan22",
            "Decoded Wan clips can be joined with a hard cut.",
            "Wan has no generation-time continuity path yet.",
            "Wan has no decoded transition path yet."))
    {
        FrameGrid = FrameGrid,
        StageGuideReferences = new(
            StageGuideReferenceKind.Generated | StageGuideReferenceKind.PreviousStage),
        Rules = [HostVideoStageRules.NormalLoraRequiresSamplingStage],
    };

    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        string modelClassId = model?.ModelClass?.ID;
        string compatClassId = model?.ModelClass?.CompatClass?.ID;
        if (!IsOrdinaryWanModel(model))
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
        resolved = new(
            model.Name,
            ArchitectureId,
            profileId,
            Descriptor)
        {
            ModelClassId = modelClassId,
            CompatibilityClassId = compatClassId,
            EntryAbilities = VideoModelEntryAbility.TextToVideo
                | VideoModelEntryAbility.ImageToVideo,
            ReferencePositions = SupportsHostEndFrame(compatClassId)
                ? ["first", "last"]
                : ["first"],
            LorasTargetTextEncoder =
                model.ModelClass.CompatClass.LorasTargetTextEnc,
            HostFactsAuthoritative = true,
        };
        return true;
    }

    internal static bool SupportsHostEndFrame(string compatibilityClassId) =>
        string.Equals(
            compatibilityClassId,
            T2IModelClassSorter.CompatWan21_14b.ID,
            StringComparison.Ordinal)
        || string.Equals(
            compatibilityClassId,
            T2IModelClassSorter.CompatWan21_1_3b.ID,
            StringComparison.Ordinal);

    private static bool IsOrdinaryWanModel(T2IModel model)
    {
        T2IModelClass modelClass = model?.ModelClass;
        T2IModelCompatClass compatibility = modelClass?.CompatClass;
        string modelClassId = modelClass?.ID ?? "";
        string compatibilityClassId = compatibility?.ID ?? "";
        if (model is null
            || modelClass is null
            || compatibility is null
            || !(compatibility.IsImage2Video || compatibility.IsText2Video)
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
        return true;
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

    public ArchitectureEffectiveRequestProjection ProjectEffectiveRequest(
        ArchitectureEffectiveRequestProjectionContext context) =>
        WanEffectiveRequestProjector.Project(context);

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

}
