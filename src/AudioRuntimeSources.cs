using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>Resolved graph-backed audio sources shared by root and clip execution.</summary>
internal sealed record AudioRuntimeSources(
    WGNodeData NativeAudio,
    IReadOnlyDictionary<int, WGNodeData> ClipAudios,
    IReadOnlyDictionary<int, WGNodeData> UploadedAudios);

/// <summary>One compiled clip entering runtime audio preparation.</summary>
internal sealed record ClipAudioExecutionContext(
    StagePlan FirstStage,
    ClipPlan PlannedClip,
    bool IsFirstClip,
    AudioRuntimeSources Sources,
    RootExecutionPolicy RootPolicy);
