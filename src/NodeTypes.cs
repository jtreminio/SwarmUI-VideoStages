namespace VideoStages;

/// <summary>
/// String constants for ComfyUI <c>class_type</c> values that the typed bindings can't represent
/// directly. Used in places where we genuinely need the string — e.g. <c>g.RunOnNodesOfClass</c>
/// dispatch, <c>media.SourceNodeData</c> string comparisons, and the in-place class-swap in
/// <c>ControlNetApplicator.ReplaceVideoControlNetUpscale</c>. Most former entries here have been
/// replaced by typed bindings (<c>VAEDecodeNode</c>, <c>ImageScaleNode</c>, …).
/// </summary>
internal static class NodeTypes
{
    public const string VAEEncode = "VAEEncode";
    public const string VAEEncodeTiled = "VAEEncodeTiled";
    public const string VAEDecodeAudio = "VAEDecodeAudio";
    public const string SwarmSaveAudioWS = "SwarmSaveAudioWS";
    public const string ResizeImageMaskNode = "ResizeImageMaskNode";
}
