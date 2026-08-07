using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
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

    // Set when the previous clip's outgoing boundary is "continue": the previous clip's tail frames at
    // their native resolution, used as this clip's opening latent context so generation picks up where
    // the prior clip ended. Each consuming stage conforms it to its own resolution.
    public WGNodeData ContinuityTail { get; set; }

    public int IncomingContinueHandleFrames { get; set; }

    public bool ContinueHandleMaterialized { get; set; }

    public int? GenerationFrames => PlannedClip.Frames is int frames
        ? frames + IncomingContinueHandleFrames
        : null;

    public bool IsFirstStage(StagePlan stage) => stage?.ClipStageIndex == 0;

    /// <summary>
    /// Whether the continue boundary's tail must be (re-)applied at this stage. The anchor is not a
    /// stage-0 artifact: any later stage that regenerates the clip's opening frames would otherwise
    /// re-denoise them free of the previous clip's tail, and at that stage's own resolution the tail
    /// no longer has to be the downscale the opening stage needed.
    /// Skipped for passthrough stages (they regenerate nothing), for retake stages (their per-frame
    /// noise mask owns what regenerates, and an inplace merge would clobber it — so a retake whose
    /// window excludes the head cannot be re-anchored), and for stages that author their own
    /// first-frame reference, which outranks an implicit boundary guide at every stage index.
    /// </summary>
    public bool ReanchorsContinuityTail(StagePlan stage) =>
        ContinuityTail is not null
        && stage is not null
        && !stage.IsPassthrough
        && !stage.HasActiveRetakeMask()
        && !stage.HasExplicitFirstFrameReference();
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
