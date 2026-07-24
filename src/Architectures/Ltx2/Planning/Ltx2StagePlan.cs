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

internal enum IcLoraMediaSourceKind
{
    Upload,
    Incoming,
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

internal enum IcLoraDriveMediaKind
{
    None,
    Image,
    Video,
    Audio,
    Unknown,
}

internal sealed record IcLoraDriveMediaPlan(
    IcLoraDriveMediaKind Kind,
    string Data,
    string FileName)
{
    internal bool IsConfigured => !string.IsNullOrWhiteSpace(Data);
}

internal sealed record IcLoraMediaInputPlan(
    IcLoraMediaSourceKind Source,
    string RawSource,
    IcLoraDriveMediaKind Kind,
    int? ControlNetIndex,
    bool HasInput);

internal sealed record IcLoraPlan(
    int EntryIndex,
    string ModelName,
    bool UsesAutoModel,
    string Preset,
    double ModelStrength,
    double AttentionStrength,
    IcLoraControlMode ControlMode,
    IcLoraDriveMediaContract MediaContract,
    IcLoraDriveMediaPlan DriveMedia,
    IcLoraMediaInputPlan MediaInput,
    int DimensionDownscaleFactor,
    double? GuideStrength,
    bool IsHdr = false)
{
    internal bool HasVisualGuide => MediaContract.ConsumesVisual && MediaInput.HasInput;

    internal bool HasAudioReference => MediaContract.ConsumesAudio && MediaInput.HasInput;
}

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
