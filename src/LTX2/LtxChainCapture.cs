using ComfyTyped.Core;
using ComfyTyped.SwarmUI;

namespace VideoStages.LTX2;

/// <summary>
/// Typed capture of the LTX post-video decode chain.
/// Replaces the JArray paths and string node IDs stored in LtxPostVideoChain.
///
/// <para>Stores node IDs (strings) rather than typed node references because the
/// pre-gen bridge becomes stale after core generation mutates the JObject.
/// IDs survive across bridges and are re-resolved on the post-gen bridge.</para>
/// </summary>
/// <param name="DecodeId">ID of the VAEDecode or VAEDecodeTiled node. Re-resolve via <c>bridge.Graph.GetNode(id)</c>.</param>
/// <param name="SeparateId">ID of the LTXVSeparateAVLatent node. Re-resolve via <c>bridge.Graph.GetNode&lt;LTXVSeparateAVLatentNode&gt;(id)</c>.</param>
/// <param name="AudioDecodeId">ID of the LTXVAudioVAEDecode node, or null if none was found.</param>
/// <param name="AudioVaeSource">Fallback audio VAE source when no AudioDecode node exists. From <c>g.CurrentAudioVae</c>.</param>
/// <param name="CurrentOutputMedia">Snapshot of the current output media at capture time.</param>
/// <param name="HasPostDecodeWrappers">True if the media path doesn't directly point to the decode node (wrappers in between).</param>
/// <param name="UseReusedAudio">True if this stage should reuse audio from a prior stage.</param>
internal sealed record LtxChainCapture(
    string DecodeId,
    string SeparateId,
    string AudioDecodeId,
    INodeOutput AudioVaeSource,
    MediaRef CurrentOutputMedia,
    bool HasPostDecodeWrappers,
    bool UseReusedAudio
);
