using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>
/// The complete graph-free instruction for one LTX stage. The common plan treats this as opaque.
/// </summary>
internal sealed record Ltx2StagePayload(
    StageCorePlan Core,
    GuideReferencePlan Guide,
    StageUpscalePlan Upscale,
    ImmutableArray<NormalLoraPlan> Loras,
    ImmutableArray<IcLoraPlan> IcLoras,
    RetakePlan Retake,
    PromptRelayPlan PromptRelay,
    ImmutableArray<ImageReferencePlan> FrameReferences,
    StageAudioAction AudioAction) : IArchitectureStagePayload
{
    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;
}

internal static class Ltx2StagePlanExtensions
{
    internal static Ltx2StagePayload RequireLtx2Payload(this StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.ArchitecturePayload is not Ltx2StagePayload payload)
        {
            throw new InvalidOperationException(
                $"Stage {stage.StageId} has no LTX architecture payload.");
        }
        return payload;
    }
}

internal sealed record StageCorePlan(
    string Model,
    double Control,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler,
    double? ControlNetStrength,
    bool ImageReferenceWasExplicit);

internal enum StageUpscaleMode
{
    None,
    Pixel,
    Model,
    Latent,
    LatentModel,
    Unsupported,
}

internal sealed record StageUpscalePlan(
    StageUpscaleMode Mode,
    double Factor,
    string RawMethod,
    string MethodName);

internal enum GuideReferenceKind
{
    Base,
    Refiner,
    Generated,
    PreviousStage,
    ExplicitStage,
    Base2Edit,
    Unknown,
}

internal sealed record GuideReferencePlan(
    GuideReferenceKind Kind,
    string RawValue,
    int? ReferencedStageIndex);

internal sealed record NormalLoraPlan(
    string Name,
    double ModelWeight,
    double TextEncoderWeight);

internal enum IcLoraDriveSourceKind
{
    UploadedMedia,
    StageInput,
    SourcedClipInput,
    ControlNet,
    LoaderOnly,
    Unknown,
}

internal enum IcLoraControlMode
{
    None,
    Canny,
    Depth,
    Normal,
    Unknown,
}

internal enum IcLoraUploadedMediaKind
{
    None,
    Image,
    Video,
    Unknown,
}

internal sealed record IcLoraDrivePlan(
    IcLoraDriveSourceKind Kind,
    string RawSource,
    int? ControlNetIndex,
    IcLoraUploadedMediaKind UploadedMediaKind,
    string UploadedData,
    bool HasDriveMedia);

internal sealed record IcLoraPlan(
    int EntryIndex,
    string ModelName,
    bool UsesAutoModel,
    string Preset,
    double ModelStrength,
    double AttentionStrength,
    IcLoraControlMode ControlMode,
    IcLoraDrivePlan Drive,
    double? GuideStrength);

internal sealed record RetakePlan(
    int StartFrame,
    int LengthFrames,
    double Strength);

internal enum PromptRelayMode
{
    None,
    SinglePromptOverride,
    Relay,
    RequiresRuntimeLength,
}

internal sealed record PromptWindowPlan(
    string Prompt,
    double StartSeconds,
    double DurationSeconds,
    double EndSeconds);

internal sealed record PromptRelaySegmentPlan(
    string Prompt,
    double Seconds);

internal sealed record PromptRelayPlan(
    PromptRelayMode Mode,
    ImmutableArray<PromptWindowPlan> AuthoredWindows,
    ImmutableArray<PromptRelaySegmentPlan> Segments);

internal enum ImageReferenceSourceKind
{
    Upload,
    Base,
    Refiner,
    Base2Edit,
    Unknown,
}

internal enum ImageReferenceFrameOrigin
{
    Start,
    End,
}

internal sealed record ImageReferencePlan(
    int Index,
    ImageReferenceSourceKind SourceKind,
    string RawSource,
    int? Base2EditStageIndex,
    int Frame,
    ImageReferenceFrameOrigin FrameOrigin,
    double Strength,
    string UploadFileName,
    string InlineData);

internal enum StageAudioAction
{
    None,
    CaptureForReuse,
    ReuseCaptured,
}
