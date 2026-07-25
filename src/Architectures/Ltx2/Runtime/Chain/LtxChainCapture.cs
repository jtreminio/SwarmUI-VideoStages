using ComfyTyped.Core;
using ComfyTyped.SwarmUI;

namespace VideoStages.Architectures.Ltx2;

internal sealed record LtxChainCapture(
    string DecodeId,
    string SeparateId,
    string AudioDecodeId,
    INodeOutput AudioVaeSource,
    MediaRef CurrentOutputMedia,
    bool HasPostDecodeWrappers
);
