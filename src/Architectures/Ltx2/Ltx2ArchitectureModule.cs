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
            ArchitectureAudioSourceKind.Native,
            ArchitectureAudioSourceKind.Upload,
            ArchitectureAudioSourceKind.VoiceReference,
            ArchitectureAudioSourceKind.ControlNet,
            ArchitectureAudioSourceKind.AceStepFun,
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
                | ClipCapability.AudioSegments,
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
        Ltx2BoundaryPolicy.Instance.PublishedRules)
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

    public IReadOnlyList<VideoPlanDiagnostic> ValidatePlan(
        IReadOnlyList<ClipPlan> architectureClips,
        IReadOnlyList<ClipPlan> timelineClips,
        RootPlan root) =>
        Ltx2VideoPlanValidationCompiler.Validate(architectureClips, timelineClips, root);

    private static VideoModelProfileDescriptor Profile(string id, string displayName) =>
        new(
            new(id),
            displayName,
            ModelProfileCapability.SamplerSelection
                | ModelProfileCapability.SchedulerSelection
                | ModelProfileCapability.DimensionRules
                | ModelProfileCapability.FrameRules
                | ModelProfileCapability.NormalLora,
            []);
}

internal sealed record Ltx2ClipPayload(
    int ClipId,
    IReadOnlyDictionary<int, Ltx2StagePayload> Stages,
    AudioVoiceReferencePlan VoiceReference,
    AudioReusePlan AudioReuse,
    int? ControlNetSourceIndex) :
    IArchitectureClipPayload,
    IArchitectureStagePayloadSource,
    IArchitectureBoundaryPolicySource,
    IArchitectureControlNetSourcePlan
{
    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

    public IArchitectureBoundaryPolicy BoundaryPolicy => Ltx2BoundaryPolicy.Instance;

    public IArchitectureStagePayload GetStagePayload(int rawStageIndex) =>
        Stages.GetValueOrDefault(rawStageIndex)
        ?? throw new InvalidOperationException(
            $"Clip {ClipId} has no LTX payload for raw stage {rawStageIndex}.");

}
