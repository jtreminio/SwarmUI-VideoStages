using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

internal sealed class WanArchitectureModule : IVideoArchitectureModule
{
    private sealed record RecognizedProfile(
        string ModelClassId,
        string CompatClassId,
        ModelProfileId ProfileId);

    internal static ArchitectureId ArchitectureId { get; } = new("wan22");

    internal const int FrameGrid = 4;

    internal const string ImageToVideoModelClassId = "wan-2_2-image2video-14b";

    internal const string Ti2v5bModelClassId = "wan-2_2-ti2v-5b";

    internal static ModelProfileId ImageToVideoProfileId { get; } = new("wan-2.2-i2v-14b");

    internal static ModelProfileId Ti2v5bProfileId { get; } = new("wan-2.2-ti2v-5b");

    internal static ModelProfileId OrdinaryImageToVideoProfileId { get; } =
        new("wan-i2v");

    private static IReadOnlyList<RecognizedProfile> ExactProfiles { get; } =
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
            ArchitectureEntryMode.InitVideo,
        ],
        ArchitectureFeature.FrameReferences,
        ArchitectureBoundaryPolicy.CutOnly(
            "wan22",
            "Decoded WAN Video clips can be joined with a hard cut."))
    {
        RunsOnStockHostSampler = true,
        FrameGrid = FrameGrid,
        StageGuideReferences = new(
            StageGuideReferenceKind.Generated | StageGuideReferenceKind.PreviousStage),
    };

    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        string modelClassId = model?.ModelClass?.ID;
        string compatClassId = model?.ModelClass?.CompatClass?.ID;
        RecognizedProfile exactProfile = ExactProfiles.SingleOrDefault(candidate =>
            string.Equals(
                modelClassId,
                candidate.ModelClassId,
                StringComparison.OrdinalIgnoreCase));
        if (!IsOrdinaryWanModel(model, exactProfile))
        {
            resolved = null;
            return false;
        }
        resolved = new(
            model.Name,
            exactProfile?.ProfileId ?? OrdinaryImageToVideoProfileId,
            Descriptor,
            modelClassId,
            compatClassId,
            SupportsHostEndFrame(compatClassId)
                ? [FrameReferencePosition.First, FrameReferencePosition.Last]
                : [FrameReferencePosition.First],
            model.ModelClass.CompatClass.LorasTargetTextEnc);
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

    private static bool IsOrdinaryWanModel(
        T2IModel model,
        RecognizedProfile exactProfile)
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
            || HasClassToken(modelClassId, "lora")
            || HasClassToken(modelClassId, "vae")
            || modelClassId.Contains("vace", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (exactProfile is not null
            && !string.Equals(
                compatibilityClassId,
                exactProfile.CompatClassId,
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

    public ArchitectureClipCompilation ValidateAndCompileClip(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ArchitectureClipCompileContext context) =>
        WanClipPlanCompiler.Compile(
            clip,
            stageModels,
            context);

}
