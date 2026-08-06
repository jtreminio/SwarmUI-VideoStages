using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>
/// The complete graph-free instruction for one LTX stage. The common plan treats this as opaque.
/// </summary>
internal sealed record Ltx2StagePayload(
    StageCorePlan Core,
    GuideReferencePlan Guide,
    bool ImageReferenceWasExplicit,
    ImmutableArray<IcLoraPlan> IcLoras,
    RetakePlan Retake,
    PromptRelayPlan PromptRelay,
    ImmutableArray<ImageReferencePlan> FrameReferences,
    StageAudioAction AudioAction) :
    IArchitectureStagePayload
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
            throw Invariant.Failure(
                $"Stage {stage.StageId} has no LTX architecture payload.");
        }
        return payload;
    }

    /// <summary>
    /// The stage's retake noise mask is the base lock, and <c>LTXVImgToVideoInplace</c> overwrites the
    /// mask of every frame it conditions, so an active retake rules out every inplace guide merge.
    /// </summary>
    internal static bool HasActiveRetakeMask(this StagePlan stage)
    {
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        return payload.Retake is not null;
    }

    /// <summary>The stage authors its own opening frame, which outranks any implicit frame-1 guide.</summary>
    internal static bool HasExplicitFirstFrameReference(this StagePlan stage) =>
        stage.RequireLtx2Payload().FrameReferences.Any(reference =>
            reference.FrameOrigin == ImageReferenceFrameEdge.Start && reference.Frame == 1);

    internal static Ltx2ClipPayload RequireLtx2Payload(this ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.ArchitecturePayload is not Ltx2ClipPayload payload)
        {
            throw Invariant.Failure(
                $"Clip {clip.ClipId} has no LTX architecture payload.");
        }
        return payload;
    }
}

internal sealed record GuideReferencePlan(
    StageGuideReferenceKind Kind,
    string RawValue,
    int? ReferencedStageIndex);

internal enum IcLoraMediaSourceKind
{
    Upload,
    Incoming,
    ControlNet,
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

internal sealed record IcLoraDrivePlan(
    IcLoraDriveData Stream,
    IcLoraMediaSourceKind Source,
    IcLoraDriveMediaKind MediaKind,
    UploadedMediaSpec Upload,
    int? ControlNetIndex);

internal sealed record IcLoraPlan(
    int EntryIndex,
    string ModelName,
    bool UsesAutoModel,
    string Preset,
    double ModelStrength,
    double AttentionStrength,
    IcLoraControlMode ControlMode,
    IcLoraDrivePlan Drive,
    int DimensionDownscaleFactor,
    double? GuideStrength)
{
    internal bool HasVisualGuide => Drive.Stream == IcLoraDriveData.Visual;

    internal bool HasAudioReference => Drive.Stream == IcLoraDriveData.Audio;
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

internal enum ImageReferenceFrameEdge
{
    Start,
    End,
}

internal sealed record ImageReferencePlan(
    ImageReferenceSourceKind SourceKind,
    string RawSource,
    int? Base2EditStageIndex,
    int Frame,
    ImageReferenceFrameEdge FrameOrigin,
    double Strength,
    string UploadFileName,
    string InlineData);

internal enum StageAudioAction
{
    None,
    CaptureForReuse,
    ReuseCaptured,
}
