using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Wan 2.2 image-to-video. Clips may chain same-profile stages over the preceding decoded stage
/// output; every other authoring feature is declared unsupported so the common capability
/// validator rejects it before graph mutation rather than an executor discovering it late.
/// </summary>
internal sealed class WanArchitectureModule : IVideoArchitectureModule
{
    internal static ArchitectureId ArchitectureId { get; } = new("wan22");

    /// <summary>
    /// Wan's VAE compresses four pixel frames into one latent frame, so authored durations and
    /// boundary windows step on four. Published as the profile's frame grid.
    /// </summary>
    internal const int FrameGrid = 4;

    /// <summary>
    /// The single host model class this slice recognizes. SwarmUI's own image-to-video builder has a
    /// dedicated branch for it, so the adapter delegates graph construction instead of restating it.
    /// </summary>
    internal const string ImageToVideoModelClassId = "wan-2_2-image2video-14b";

    internal static ModelProfileId ImageToVideoProfileId { get; } = new("wan-2.2-i2v-14b");

    internal static WanArchitectureModule Instance { get; } = new();

    public VideoArchitectureDescriptor Descriptor { get; } = new(
        ArchitectureId,
        "Wan 2.2",
        ImageToVideoProfileId,
        [ArchitectureEntryMode.ImageToVideo],
        [AudioSourceKind.Disabled],
        [
            new(
                ImageToVideoProfileId,
                "Wan 2.2 Image2Video 14B",
                ModelProfileCapability.SamplerSelection
                    | ModelProfileCapability.SchedulerSelection
                    | ModelProfileCapability.DimensionRules
                    | ModelProfileCapability.FrameRules,
                [])
            {
                FrameGrid = FrameGrid,
            },
        ],
        new(
            ArchitectureCapability.GeneratedEntry
                | ArchitectureCapability.MultiStage
                | ArchitectureCapability.DecodedOutput,
            ClipCapability.Prompts,
            StageCapability.ImageInput | StageCapability.VideoInput,
            OutputCapability.Video),
        WanBoundaryPolicy.Instance)
    {
        StageGuideReferences = new(
            StageGuideReferenceKind.Generated | StageGuideReferenceKind.PreviousStage),
    };

    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        if (model?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatWan21_14b.ID
            || !string.Equals(
                model.ModelClass.ID,
                ImageToVideoModelClassId,
                StringComparison.OrdinalIgnoreCase))
        {
            resolved = null;
            return false;
        }
        resolved = new(
            model.Name,
            ArchitectureId,
            ImageToVideoProfileId,
            Descriptor);
        return true;
    }

    public ArchitectureClipCompilation ValidateAndCompileClip(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ArchitectureClipCompileContext context)
    {
        WanClipPlanCompilation compilation = WanClipPlanCompiler.Compile(
            clip,
            stageModels);
        return new(
            compilation.Payload,
            compilation.Stages.ToDictionary(
                pair => pair.Key,
                pair => (IArchitectureStagePayload)pair.Value),
            compilation.Diagnostics);
    }
}
