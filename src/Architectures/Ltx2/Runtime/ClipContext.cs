using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class ClipContext
{
    public ClipContext(
        VideoExecutionPlan plan,
        ClipPlan plannedClip,
        WGNodeData sourceMedia,
        WGNodeData sourceVae)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        PlannedClip = plannedClip ?? throw new ArgumentNullException(nameof(plannedClip));
        SourceMedia = sourceMedia;
        SourceVae = sourceVae;
        Dimensions = new ClipDimensionState
        {
            Width = plan.Width,
            Height = plan.Height
        };
    }

    public VideoExecutionPlan Plan { get; }
    public ClipPlan PlannedClip { get; }
    public ClipDimensionState Dimensions { get; }
    public WGNodeData SourceMedia { get; }
    public WGNodeData SourceVae { get; }
    /// <summary>
    /// Contextual media exposed to stage-0 IC-LoRAs using DriveSource=Incoming. A clip's own source
    /// video wins; otherwise this is the previous clip's decoded output, then the host entry media.
    /// It does not replace the clip's normal sampler input.
    /// </summary>
    public WGNodeData IcLoraEntryIncomingMedia { get; set; }
    public Ltx2ClipAudioReuseState AudioReuse { get; } = new();
    public LtxPendingAudioConditioningState PendingAudioConditioning { get; } = new();

    // Set when the previous clip's outgoing boundary is "continue": the previous clip's final rendered
    // frame, used as this clip's first-frame guide so generation picks up where the prior clip ended.
    public WGNodeData ContinuityFrame { get; set; }

    public bool IsFirstStage(StagePlan stage) => stage?.ClipStageIndex == 0;
}

internal sealed class LtxPendingAudioConditioningState
{
    private IReadOnlyList<(double Start, double End)> _preserveWindows = [];

    public WGNodeData Audio { get; private set; }
    public IReadOnlyList<(double Start, double End)> PreserveWindows => _preserveWindows;
    public bool IsPending => Audio is not null && _preserveWindows.Count > 0;

    public void Defer(
        WGNodeData audio,
        IReadOnlyList<(double Start, double End)> preserveWindows)
    {
        Audio = audio;
        _preserveWindows = preserveWindows is { Count: > 0 }
            ? [.. preserveWindows]
            : [];
    }

    public void Clear()
    {
        Audio = null;
        _preserveWindows = [];
    }
}

internal sealed class ClipDimensionState
{
    public int Width;
    public int Height;
    public bool HasLatentUpscale;
}
