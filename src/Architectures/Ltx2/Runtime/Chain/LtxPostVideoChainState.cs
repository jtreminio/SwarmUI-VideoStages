using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2;

internal sealed record LtxPostVideoChainState(
    WGNodeData CurrentOutputMedia,
    JArray AvLatentPath,
    JArray AudioLatentPath,
    JArray VideoVaePath,
    JArray AudioVaePath,
    string VideoDecodeNodeId,
    string AudioDecodeNodeId,
    JArray DecodeOutputPath,
    bool HasPostDecodeWrappers,
    bool UseReusedAudioLatent);
