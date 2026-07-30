using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

internal sealed record WanFrameReferencePlan(
    string Source,
    string UploadFileName,
    string InlineData);

/// <summary>
/// The complete graph-free instruction for one Wan stage. The common plan treats this as opaque.
/// <c>Control</c> is regeneration strength, so a lower positive value starts sampling later in the
/// schedule; exact zero is a samplerless passthrough for a decoded input.
/// </summary>
internal sealed record WanStagePayload(
    string Model,
    double Control,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler,
    StageUpscalePlan Upscale,
    ImmutableArray<NormalLoraPlan> Loras) :
    IArchitectureStagePayload,
    IHostVideoStageSettings
{
    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;

    public string ModelClassId { get; init; } = Model;

    public string CompatibilityClassId { get; init; } =
        WanArchitectureModule.ArchitectureId.Value;
}

internal sealed record WanClipPayload(
    int ClipId,
    WanFrameReferencePlan FirstFrameReference = null,
    WanFrameReferencePlan LastFrameReference = null) :
    IArchitectureClipPayload,
    IArchitectureClipGeometryProjection
{
    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;

    public string CompatibilityClassId { get; init; } =
        WanArchitectureModule.ArchitectureId.Value;

    public (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height)
    {
        foreach (StagePlan stage in stages ?? [])
        {
            StageUpscalePlan upscale = stage.RequireWanPayload().Upscale;
            if (upscale?.Mode != StageUpscaleMode.Pixel
                || stage.Input is not (
                    StageInputKind.SourceVideo
                    or StageInputKind.PreviousStage))
            {
                continue;
            }
            (width, height) = DimensionSnap.Snap(
                width * upscale.Factor,
                height * upscale.Factor);
        }
        return (width, height);
    }
}

internal static class WanClipPlanExtensions
{
    internal static WanStagePayload RequireWanPayload(this StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.ArchitecturePayload is not WanStagePayload payload)
        {
            throw new InvalidOperationException(
                $"Stage {stage.StageId} has no Wan architecture payload.");
        }
        return payload;
    }

    internal static WanClipPayload RequireWanPayload(this ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.ArchitecturePayload is not WanClipPayload payload)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} has no Wan architecture payload.");
        }
        return payload;
    }
}
