using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal sealed record StageSequenceRootSources(
    WGNodeData SourceMedia,
    WGNodeData SourceVae,
    AudioRuntimeSources AudioSources);
