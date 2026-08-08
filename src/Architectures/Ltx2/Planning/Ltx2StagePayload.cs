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
    bool ImageReferenceWasExplicit,
    ImmutableArray<IcLoraPlan> IcLoras,
    RetakePlan Retake,
    PromptRelayPlan PromptRelay,
    ImmutableArray<FrameRefPlan> FrameReferences,
    StageAudioAction AudioAction) :
    IArchitectureStagePayload
{
    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;
}

internal sealed record GuideReferencePlan(
    StageGuideReferenceKind Kind,
    string RawValue,
    int? ReferencedStageIndex);

internal sealed record RetakePlan(
    int StartFrame,
    int LengthFrames,
    double Strength);

internal enum StageAudioAction
{
    None,
    CaptureForReuse,
    ReuseCaptured,
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
        stage.RequireLtx2Payload().FrameReferences.Any(reference => reference.IsOpeningFrame);
}
