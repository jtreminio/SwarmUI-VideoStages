using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

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
    public ConditioningHandoff LastConditioningHandoff { get; set; }
    public JArray CachedControlNetLoraModelPath { get; set; }
    public ClipAudioState AudioReuse { get; } = new();

    public bool IsFirstStage(StageSpec stage) =>
        Clip.Stages.Count > 0 && Clip.Stages[0].Id == stage.Id;
}

internal sealed record ConditioningHandoff(
    int ClipId,
    JArray Positive,
    JArray Negative);

internal sealed class ClipDimensionState
{
    public int Width;
    public int Height;
}
