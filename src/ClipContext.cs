using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages;

internal sealed class ClipContext
{
    public ClipContext(ClipSpec clip, int width, int height, WGNodeData sourceMedia, WGNodeData sourceVae)
    {
        Clip = clip;
        SourceMedia = sourceMedia;
        SourceVae = sourceVae;
        Dimensions = new ClipDimensionState
        {
            Width = width,
            Height = height
        };
    }

    public ClipSpec Clip { get; }
    public ClipDimensionState Dimensions { get; }
    public WGNodeData SourceMedia { get; }
    public WGNodeData SourceVae { get; }
    public ClipAudioState AudioReuse { get; } = new();

    // Set when the previous clip's outgoing boundary is "continue": the previous clip's final rendered
    // frame, used as this clip's first-frame guide so generation picks up where the prior clip ended.
    public WGNodeData ContinuityFrame { get; set; }

    public bool IsFirstStage(StagePlan stage) => stage?.ClipStageIndex == 0;
}

internal sealed class ClipDimensionState
{
    public int Width;
    public int Height;
}
