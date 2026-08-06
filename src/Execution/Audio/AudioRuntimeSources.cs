using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Execution.Audio;

/// <summary>Resolved graph-backed audio sources shared by root and clip execution.</summary>
internal sealed record AudioRuntimeSources(
    WGNodeData NativeAudio,
    IReadOnlyDictionary<int, WGNodeData> ClipAudios,
    IReadOnlyDictionary<int, WGNodeData> UploadedAudios);
