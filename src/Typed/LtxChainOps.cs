using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Typed;

/// <summary>
/// Typed implementations of LTX post-video chain operations.
/// These replace the JObject-walking logic in <see cref="LTX2.LtxPostVideoChain"/>
/// with typed ComfyGraph queries and mutations.
///
/// All methods are static and operate on a <see cref="WorkflowBridge"/> + <see cref="MediaRef"/>.
/// Side effects (audio reuse state, g.CurrentMedia updates) happen at the boundary, not here.
/// </summary>
internal static class LtxChainOps
{
    // ── TryCapture ─────────────────────────────────────────────────

    /// <summary>
    /// Typed equivalent of LtxPostVideoChain.TryCapture.
    /// Queries the pre-generation workflow to find the VAEDecode → LTXVSeparateAVLatent
    /// → KSampler chain and captures node IDs for post-generation splice.
    ///
    /// Returns null if the expected chain pattern is not found.
    /// </summary>
    public static LtxChainCapture TryCapture(
        WorkflowBridge bridge,
        MediaRef currentMedia,
        MediaRef currentAudioVae,
        bool useReusedAudio)
    {
        if (currentMedia?.Output?.Node is not ComfyNode mediaNode)
            return null;

        // Find VAEDecode or VAEDecodeTiled: check the node itself first (media may point
        // directly to decode output), then walk upstream (when there are post-decode wrappers).
        // Replaces: WorkflowUtils.TryResolveNearestUpstreamDecode + StringUtils.NodeTypeMatches
        ComfyNode decode = mediaNode as VAEDecodeNode
                        ?? mediaNode as VAEDecodeTiledNode
                        ?? bridge.Graph.FindNearestUpstream<VAEDecodeNode>(mediaNode)
                        ?? (ComfyNode)bridge.Graph.FindNearestUpstream<VAEDecodeTiledNode>(mediaNode);
        if (decode is null)
            return null;

        // Follow samples connection to LTXVSeparateAVLatent
        // Replaces: LtxVaeDecodeInputs.TryGetDecodeSamplesRef + manual node lookup + NodeTypeMatches
        INodeInput samplesInput = decode.FindInput("samples");
        if (samplesInput?.Connection?.Node is not LTXVSeparateAVLatentNode separate)
            return null;

        // Verify av_latent and vae connections exist
        if (separate.AvLatent.Connection is null)
            return null;
        INodeInput vaeInput = decode.FindInput("vae");
        if (vaeInput?.Connection is null)
            return null;

        // Find audio decode connected to this separate node's audio output (slot 1)
        // Replaces: TryFindAudioDecode 35-line linear scan
        LTXVAudioVAEDecodeNode audioDecode = bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>()
            .FirstOrDefault(n => n.Samples.Connection?.Node == separate
                              && n.Samples.Connection?.SlotIndex == 1);

        // Audio VAE source: from audio decode if found, otherwise from currentAudioVae
        INodeOutput audioVaeSource = audioDecode?.AudioVae.Connection
                                  ?? currentAudioVae?.Output;
        if (audioVaeSource is null)
            return null;

        bool hasPostDecodeWrappers = !ReferenceEquals(currentMedia.Output.Node, decode);

        return new LtxChainCapture(
            DecodeId: decode.Id,
            SeparateId: separate.Id,
            AudioDecodeId: audioDecode?.Id,
            AudioVaeSource: audioVaeSource,
            CurrentOutputMedia: currentMedia.Clone(),
            HasPostDecodeWrappers: hasPostDecodeWrappers,
            UseReusedAudio: useReusedAudio);
    }

    // ── SpliceCurrentOutput ────────────────────────────────────────

    /// <summary>
    /// Parameters for video decode node configuration.
    /// Extracted from g.UserInput at the boundary.
    /// </summary>
    internal sealed record DecodeConfig(
        bool UseTiledDecode,
        int TileSize = 768,
        int Overlap = 64,
        int TemporalSize = 4096,
        int TemporalOverlap = 4);

    /// <summary>
    /// Typed equivalent of LtxPostVideoChain.SpliceCurrentOutput.
    /// Creates a new LTXVSeparateAVLatent node for the stage output,
    /// retargets the video decode to use it, and retargets audio decode connections.
    ///
    /// Returns the MediaRef for the current output (pointing to the original decode output).
    /// </summary>
    public static MediaRef SpliceCurrentOutput(
        WorkflowBridge bridge,
        LtxChainCapture capture,
        MediaRef stageOutput,
        MediaRef vae,
        DecodeConfig decodeConfig)
    {
        if (stageOutput?.Output is null)
            return null;

        // Create new LTXVSeparateAVLatent for this stage's output
        // Replaces: g.CreateNode(LtxNodeTypes.LTXVSeparateAVLatent, new JObject{["av_latent"] = stageOutputPath})
        var newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectToUntyped(stageOutput.Output);
        bridge.SyncNode(newSeparate);

        // Replace the existing video decode in place: remove the old node and add a fresh
        // VAEDecode or VAEDecodeTiled under the same ID. Preserving the ID keeps every
        // [decodeId, slot] reference (g.CurrentMedia, save nodes, metadata) valid without
        // rewriting the JObject side.
        ReplaceVideoDecode(
            bridge,
            capture.DecodeId,
            vae,
            newSeparate,
            decodeConfig);

        // Retarget audio decode to use new separate's audio output
        // Replaces: WorkflowUtils.RetargetInputConnections with predicate
        if (capture.AudioDecodeId is not null)
        {
            LTXVSeparateAVLatentNode oldSeparate =
                bridge.Graph.GetNode<LTXVSeparateAVLatentNode>(capture.SeparateId);
            if (oldSeparate is not null)
            {
                int retargeted = bridge.Graph.RetargetConnections(
                    oldSeparate.AudioLatent,
                    newSeparate.AudioLatent,
                    (node, input) => node.Id == capture.AudioDecodeId
                                  && input.Name == "samples");
                if (retargeted > 0)
                    bridge.SyncNode(capture.AudioDecodeId);
            }

            if (!HasAudioDecodeConnectedToSeparate(bridge, capture.AudioDecodeId, newSeparate.Id))
            {
                RetargetCapturedAudioDecodeViaJObject(bridge, capture.AudioDecodeId, newSeparate);
            }
        }

        return capture.CurrentOutputMedia.Clone();
    }

    /// <summary>
    /// Typed equivalent of LtxPostVideoChain.SpliceCurrentOutputToDedicatedBranch.
    /// Creates a dedicated decode branch for parallel multi-clip workflows.
    /// </summary>
    public static MediaRef SpliceCurrentOutputToDedicatedBranch(
        WorkflowBridge bridge,
        LtxChainCapture capture,
        MediaRef stageOutput,
        MediaRef vae,
        DecodeConfig decodeConfig,
        int outputWidth,
        int outputHeight,
        int? outputFrames,
        int? outputFps)
    {
        if (stageOutput?.Output is null)
            return null;

        // Create new separate node
        var newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectToUntyped(stageOutput.Output);
        bridge.SyncNode(newSeparate);

        if (vae?.Output is null)
            return null;

        // Create dedicated video decode (typed; tiled or basic by user setting)
        ComfyNode dedicatedDecode = AddDecode(bridge, vae.Output, newSeparate.VideoLatent, decodeConfig);

        // Create dedicated audio decode
        var dedicatedAudioDecode = bridge.AddNode(new LTXVAudioVAEDecodeNode());
        dedicatedAudioDecode.Samples.ConnectTo(newSeparate.AudioLatent);
        if (capture.AudioVaeSource is not null)
            dedicatedAudioDecode.AudioVae.ConnectToUntyped(capture.AudioVaeSource);
        bridge.SyncNode(dedicatedAudioDecode);

        MediaRef decodedVideo = new()
        {
            Output = dedicatedDecode.Outputs[0],
            DataType = WGNodeData.DT_VIDEO,
            Compat = vae?.Compat ?? capture.CurrentOutputMedia.Compat,
            Width = outputWidth,
            Height = outputHeight,
            Frames = outputFrames ?? capture.CurrentOutputMedia.Frames,
            FPS = outputFps ?? capture.CurrentOutputMedia.FPS,
            AttachedAudio = new MediaRef
            {
                Output = dedicatedAudioDecode.Audio,
                DataType = WGNodeData.DT_AUDIO,
                Compat = capture.AudioVaeSource?.Node is ComfyNode audioVaeNode
                    ? capture.CurrentOutputMedia.Compat
                    : null
            }
        };

        return decodedVideo;
    }

    // ── AttachDecodedLtxAudioFromCurrentVideo ──────────────────────

    /// <summary>
    /// Typed equivalent of LtxStageExecutor.AttachDecodedLtxAudioFromCurrentVideo.
    /// If currentMedia points to a VAEDecode whose samples come from an
    /// LTXVSeparateAVLatent, creates an audio decode for the audio latent.
    /// </summary>
    public static void AttachDecodedLtxAudio(
        WorkflowBridge bridge,
        MediaRef currentMedia,
        MediaRef audioVae)
    {
        if (currentMedia?.Output?.Node is null || audioVae?.Output is null)
            return;

        // Check if output comes from a VAEDecode or VAEDecodeTiled
        ComfyNode decodeNode = currentMedia.Output.Node;
        INodeInput samplesInput = decodeNode.FindInput("samples");
        if (samplesInput is null)
            return;

        bool isDecode = decodeNode is VAEDecodeNode or VAEDecodeTiledNode;
        if (!isDecode)
            return;

        // Check if samples come from an LTXVSeparateAVLatent
        if (samplesInput.Connection?.Node is not LTXVSeparateAVLatentNode separate)
            return;

        // Create audio decode node
        var audioDecode = bridge.AddNode(new LTXVAudioVAEDecodeNode());
        audioDecode.Samples.ConnectTo(separate.AudioLatent);
        audioDecode.AudioVae.ConnectToUntyped(audioVae.Output);
        bridge.SyncNode(audioDecode);

        currentMedia.AttachedAudio = new MediaRef
        {
            Output = audioDecode.Audio,
            DataType = WGNodeData.DT_AUDIO,
            Compat = audioVae.Compat
        };
    }

    // ── RetargetAnimationSaves ─────────────────────────────────────

    /// <summary>
    /// Typed equivalent of LtxPostVideoChain.RetargetAnimationSaves.
    /// Retargets all SwarmSaveAnimationWS.images inputs from oldOutput to newOutput.
    /// </summary>
    public static void RetargetAnimationSaves(
        WorkflowBridge bridge,
        INodeOutput oldOutput,
        INodeOutput newOutput)
    {
        if (oldOutput is null || newOutput is null)
            return;

        bridge.Graph.RetargetConnections(
            oldOutput,
            newOutput,
            (node, input) => node is SwarmSaveAnimationWSNode && input.Name == "images");

        // Sync all affected save nodes
        foreach (ComfyNode downstream in bridge.Graph.FindDownstream(newOutput))
        {
            if (downstream is SwarmSaveAnimationWSNode)
                bridge.SyncNode(downstream);
        }
    }

    // ── Private helpers ────────────────────────────────────────────

    /// <summary>
    /// Replace the existing decode node with a fresh typed VAEDecode or VAEDecodeTiled,
    /// preserving the original ID so all [decodeId, slot] references stay valid.
    /// Retargets typed-side downstream connections from the old IMAGE output to the new one.
    /// </summary>
    private static void ReplaceVideoDecode(
        WorkflowBridge bridge,
        string decodeId,
        MediaRef vae,
        LTXVSeparateAVLatentNode newSeparate,
        DecodeConfig decodeConfig)
    {
        if (string.IsNullOrWhiteSpace(decodeId) || vae?.Output is null)
            return;

        ComfyNode oldDecode = bridge.Graph.GetNode(decodeId);
        if (oldDecode is null)
            return;

        INodeOutput oldImageOutput = oldDecode.Outputs[0];
        bridge.RemoveNode(decodeId);

        ComfyNode newDecode = AddDecode(
            bridge, vae.Output, newSeparate.VideoLatent, decodeConfig, preserveId: decodeId);

        bridge.Graph.RetargetConnections(oldImageOutput, newDecode.Outputs[0]);
    }

    /// <summary>
    /// Add a typed VAEDecode or VAEDecodeTiled to the graph, wired to the given vae and samples
    /// outputs. If <paramref name="preserveId"/> is provided, the node takes that exact ID.
    /// </summary>
    private static ComfyNode AddDecode(
        WorkflowBridge bridge,
        INodeOutput vaeOutput,
        INodeOutput samplesOutput,
        DecodeConfig config,
        string preserveId = null)
    {
        if (config.UseTiledDecode)
        {
            VAEDecodeTiledNode tiled = new();
            tiled.TileSize.Set((long)config.TileSize);
            tiled.Overlap.Set((long)config.Overlap);
            tiled.TemporalSize.Set((long)config.TemporalSize);
            tiled.TemporalOverlap.Set((long)config.TemporalOverlap);
            VAEDecodeTiledNode added = preserveId is not null
                ? bridge.AddNode(tiled, preserveId)
                : bridge.AddNode(tiled);
            added.Vae.ConnectToUntyped(vaeOutput);
            added.Samples.ConnectToUntyped(samplesOutput);
            bridge.SyncNode(added);
            return added;
        }

        VAEDecodeNode basic = new();
        VAEDecodeNode addedBasic = preserveId is not null
            ? bridge.AddNode(basic, preserveId)
            : bridge.AddNode(basic);
        addedBasic.Vae.ConnectToUntyped(vaeOutput);
        addedBasic.Samples.ConnectToUntyped(samplesOutput);
        bridge.SyncNode(addedBasic);
        return addedBasic;
    }

    private static void RetargetCapturedAudioDecodeViaJObject(
        WorkflowBridge bridge,
        string audioDecodeId,
        LTXVSeparateAVLatentNode newSeparate)
    {
        if (string.IsNullOrWhiteSpace(audioDecodeId))
            return;

        if (bridge.Workflow[audioDecodeId] is not JObject audioDecode)
            return;

        JObject inputs = audioDecode["inputs"] as JObject;
        if (inputs is null)
        {
            inputs = new JObject();
            audioDecode["inputs"] = inputs;
        }

        JArray samplesRef = WorkflowBridge.ToPath(newSeparate.AudioLatent);
        inputs["samples"] = new JArray(samplesRef[0], samplesRef[1]);
    }

    private static bool HasAudioDecodeConnectedToSeparate(
        WorkflowBridge bridge,
        string audioDecodeId,
        string separateId)
    {
        ComfyNode audioNode = bridge.Graph.GetNode(audioDecodeId);
        if (audioNode is null)
            return false;

        INodeInput samplesInput = audioNode.FindInput("samples");
        return samplesInput?.Connection?.Node?.Id == separateId;
    }

}
