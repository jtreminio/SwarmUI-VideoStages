using ComfyTyped.Core;
using ComfyTyped.SwarmUI;

namespace VideoStages.LTX2;

internal sealed record LtxChainCapture(
    string DecodeId,
    string SeparateId,
    string AudioDecodeId,
    INodeOutput AudioVaeSource,
    MediaRef CurrentOutputMedia,
    bool HasPostDecodeWrappers,
    bool UseReusedAudio
);
