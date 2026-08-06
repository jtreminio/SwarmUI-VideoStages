using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Audio;

namespace VideoStages.Execution;

internal sealed record StageSequenceRootSources(
    WGNodeData SourceMedia,
    WGNodeData SourceVae,
    AudioRuntimeSources AudioSources);
