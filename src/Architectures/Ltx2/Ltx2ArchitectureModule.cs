using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class Ltx2ArchitectureModule :
    IVideoArchitectureModule,
    IArchitecturePlanValidator
{
    internal static ArchitectureId ArchitectureId { get; } = new("ltx2");

    /// <summary>The one declaration of the LTX frame grid; every other use reads it from here.</summary>
    internal const int FrameGrid = 8;

    /// <summary>
    /// LTX's pixel→latent temporal mapping: the first pixel frame owns a latent frame of its own and
    /// every further <see cref="FrameGrid"/> pixel frames add one. Mirrored in the Comfy node as
    /// <c>swarm_prompt_relay.prompt_relay.pixel_to_latent_frames</c>; the pair is pinned by
    /// <c>Tests/fixtures/latent-frame-cases.json</c>.
    /// </summary>
    internal static int LatentFrameCount(int pixelFrames) =>
        (Math.Max(1, pixelFrames) - 1) / FrameGrid + 1;

    internal static Ltx2ArchitectureModule Instance { get; } = new();

    public VideoArchitectureDescriptor Descriptor { get; } = new(
        ArchitectureId,
        "LTX Video 2.3",
        new("ltx-2.3"),
        [
            ArchitectureEntryMode.TextToVideo,
            ArchitectureEntryMode.ImageToVideo,
            ArchitectureEntryMode.SourceVideo,
            ArchitectureEntryMode.RefineVideo,
        ],
        [
            AudioSourceKind.Native,
            AudioSourceKind.Upload,
            AudioSourceKind.ControlNet,
            AudioSourceKind.AceStepFun,
        ],
        [
            Profile("ltx-2.3", "LTX Video 2.3"),
        ],
        new(
            ArchitectureCapability.GeneratedEntry
                | ArchitectureCapability.SourcedEntry
                | ArchitectureCapability.MultiStage
                | ArchitectureCapability.NativeAudio
                | ArchitectureCapability.DecodedOutput,
            ClipCapability.SourceVideo
                | ClipCapability.Prompts
                | ClipCapability.PromptRelay
                | ClipCapability.References
                | ClipCapability.Retake
                | ClipCapability.AudioSources
                | ClipCapability.AudioSegments
                | ClipCapability.ReferenceFraming,
            StageCapability.ImageInput
                | StageCapability.VideoInput
                | StageCapability.PixelUpscale
                | StageCapability.ModelUpscale
                | StageCapability.LatentUpscale
                | StageCapability.LatentModelUpscale
                | StageCapability.Lora
                | StageCapability.IcLora
                | StageCapability.Hdr
                | StageCapability.FrameReferences,
            OutputCapability.Video
                | OutputCapability.AttachedAudio
                | OutputCapability.StandaloneAudio),
        Ltx2BoundaryPolicy.Instance)
    {
        Rules = Ltx2ConditionalRulePolicySource.PublishedRules,
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
            ArchitectureId,
            new("ltx-2.3"),
            model.ModelClass.CompatClass.ID,
            Descriptor);
        return true;
    }

    public ArchitectureClipCompilation ValidateAndCompileClip(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ArchitectureClipCompileContext context)
    {
        Ltx2ClipPlanCompilation compilation = Ltx2ClipPlanCompiler.Compile(clip, context);
        return new(compilation.Payload, compilation.Diagnostics);
    }

    public IReadOnlyList<PlanDiagnostic> ValidatePlan(
        IReadOnlyList<ClipPlan> architectureClips,
        IReadOnlyList<ClipPlan> timelineClips,
        RootPlan root) =>
        Ltx2ConditionalRulePolicySource.Validate(architectureClips, timelineClips, root);

    private static VideoModelProfileDescriptor Profile(string id, string displayName) =>
        new(
            new(id),
            displayName,
            ModelProfileCapability.SamplerSelection
                | ModelProfileCapability.SchedulerSelection
                | ModelProfileCapability.DimensionRules
                | ModelProfileCapability.FrameRules
                | ModelProfileCapability.NormalLora,
            [])
        {
            FrameGrid = FrameGrid,
        };
}

internal sealed record Ltx2ClipPayload(
    int ClipId,
    IReadOnlyDictionary<int, Ltx2StagePayload> Stages,
    AudioReusePlan AudioReuse,
    Ltx2AudioInjectionPlan AudioInjection,
    int? ControlNetSourceIndex,
    ReferenceFramingMode ReferenceFraming) :
    IArchitectureClipPayload,
    IArchitectureStagePayloadSource,
    IArchitectureControlNetSourcePlan,
    IArchitectureClipGeometryProjection
{
    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

    /// <summary>
    /// Replays the runtime upscale rules over the authored stage chain: latent upscales apply in
    /// latent space and, once one has run, later pixel/model requests are ignored.
    /// </summary>
    public (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height)
    {
        bool hasLatentUpscale = false;
        foreach (StagePlan stage in stages ?? [])
        {
            StageUpscalePlan upscale = stage.RequireLtx2Payload().Upscale;
            bool isLatent = upscale.Mode is StageUpscaleMode.Latent or StageUpscaleMode.LatentModel;
            if (!isLatent
                && (upscale.Mode is not (StageUpscaleMode.Pixel or StageUpscaleMode.Model)
                    || hasLatentUpscale
                    || string.IsNullOrWhiteSpace(upscale.RawMethod)))
            {
                continue;
            }
            (width, height) = StageDimensionRules.ResolveUpscaled(stage, width, height);
            hasLatentUpscale |= isLatent;
        }
        return (width, height);
    }

    public IArchitectureStagePayload GetStagePayload(int rawStageIndex) =>
        Stages.GetValueOrDefault(rawStageIndex)
        ?? throw new InvalidOperationException(
            $"Clip {ClipId} has no LTX payload for raw stage {rawStageIndex}.");

}
