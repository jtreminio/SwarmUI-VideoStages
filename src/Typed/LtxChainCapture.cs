using ComfyTyped.Core;
using ComfyTyped.SwarmUI;

namespace VideoStages.Typed;

/// <summary>
/// Typed capture of the LTX post-video decode chain.
/// Replaces the JArray paths and string node IDs stored in LtxPostVideoChain.
///
/// Stores node IDs (strings) rather than typed node references because the
/// pre-gen bridge becomes stale after core generation mutates the JObject.
/// IDs survive across bridges and are re-resolved on the post-gen bridge.
/// </summary>
internal sealed record LtxChainCapture(
    /// <summary>ID of the VAEDecode or VAEDecodeTiled node. Re-resolve via bridge.Graph.GetNode(id).</summary>
    string DecodeId,

    /// <summary>ID of the LTXVSeparateAVLatent node. Re-resolve via bridge.Graph.GetNode&lt;LTXVSeparateAVLatentNode&gt;(id).</summary>
    string SeparateId,

    /// <summary>ID of the LTXVAudioVAEDecode node, or null if none was found.</summary>
    string AudioDecodeId,

    /// <summary>Fallback audio VAE source when no AudioDecode node exists. From g.CurrentAudioVae.</summary>
    INodeOutput AudioVaeSource,

    /// <summary>Snapshot of the current output media at capture time.</summary>
    MediaRef CurrentOutputMedia,

    /// <summary>True if the media path doesn't directly point to the decode node (wrappers in between).</summary>
    bool HasPostDecodeWrappers,

    /// <summary>True if this stage should reuse audio from a prior stage.</summary>
    bool UseReusedAudio
);
