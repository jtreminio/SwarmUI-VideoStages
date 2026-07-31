using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed record LtxChainCapture(
    string DecodeId,
    string SeparateId,
    string AudioDecodeId,
    INodeOutput AudioVaeSource,
    MediaRef CurrentOutputMedia,
    bool HasPostDecodeWrappers
);

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

/// <summary>
/// Performs read-only inspection of the graph around the current LTX video output.
/// </summary>
internal static class LtxPostVideoChainInspector
{
    public static LtxPostVideoChainState TryCapture(
        WorkflowGenerator generator,
        bool useReusedAudio)
    {
        if (generator.CurrentMedia?.IsRawMedia != true
            || generator.CurrentMedia.Path is not JArray { Count: 2 })
        {
            return null;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        MediaRef currentMedia = MediaRef.FromWGNodeData(generator.CurrentMedia, bridge);
        MediaRef currentAudioVae = MediaRef.FromWGNodeData(generator.CurrentAudioVae, bridge);
        LtxChainCapture capture =
            TryCapture(bridge, currentMedia, currentAudioVae, useReusedAudio);
        if (capture is null)
        {
            return null;
        }

        LTXVSeparateAVLatentNode separate =
            bridge.Graph.GetNode<LTXVSeparateAVLatentNode>(capture.SeparateId);
        ComfyNode decode = bridge.Graph.GetNode(capture.DecodeId);
        JArray avLatentPath = separate?.AvLatent.Connection is not null
            ? WorkflowBridge.ToPath(separate.AvLatent.Connection)
            : null;
        JArray videoVaePath = decode?.FindInput("vae")?.Connection is INodeOutput vaeOutput
            ? WorkflowBridge.ToPath(vaeOutput)
            : null;
        JArray audioVaePath = capture.AudioVaeSource is not null
            ? WorkflowBridge.ToPath(capture.AudioVaeSource)
            : null;
        if (avLatentPath is null || videoVaePath is null || audioVaePath is null)
        {
            return null;
        }

        return new LtxPostVideoChainState(
            LtxStageInputArtifactFactory.CloneMedia(generator, generator.CurrentMedia),
            avLatentPath,
            new JArray(capture.SeparateId, 1),
            videoVaePath,
            audioVaePath,
            capture.DecodeId,
            capture.AudioDecodeId,
            new JArray(capture.DecodeId, 0),
            capture.HasPostDecodeWrappers,
            useReusedAudio);
    }

    public static LtxChainCapture TryCapture(
        WorkflowBridge bridge,
        MediaRef currentMedia,
        MediaRef currentAudioVae,
        bool useReusedAudio)
    {
        if (currentMedia?.Output?.Node is not ComfyNode mediaNode)
        {
            return null;
        }

        IVaeDecode decode = mediaNode as IVaeDecode
            ?? bridge.Graph.FindNearestUpstream<IVaeDecode>(mediaNode);
        if (decode is null
            || decode.Samples.Connection?.Node is not LTXVSeparateAVLatentNode separate
            || separate.AvLatent.Connection is null
            || decode.Vae.Connection is null)
        {
            return null;
        }

        LTXVAudioVAEDecodeNode audioDecode = bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>()
            .FirstOrDefault(n =>
                n.Samples.Connection?.Node == separate
                && n.Samples.Connection?.SlotIndex == 1);

        INodeOutput audioVaeSource = audioDecode?.AudioVae.Connection ?? currentAudioVae?.Output;
        if (audioVaeSource is null)
        {
            return null;
        }

        return new LtxChainCapture(
            DecodeId: decode.Id,
            SeparateId: separate.Id,
            AudioDecodeId: audioDecode?.Id,
            AudioVaeSource: audioVaeSource,
            CurrentOutputMedia: currentMedia.Clone(),
            HasPostDecodeWrappers: !ReferenceEquals(currentMedia.Output.Node, decode));
    }

    public static LtxChainCapture Rehydrate(
        LtxPostVideoChainState state,
        WorkflowBridge bridge)
    {
        INodeOutput audioVaeSource = state.AudioVaePath is JArray { Count: 2 } audioVaePath
            ? bridge.ResolvePath(audioVaePath)
            : null;

        return new LtxChainCapture(
            DecodeId: state.VideoDecodeNodeId,
            SeparateId: $"{state.AudioLatentPath[0]}",
            AudioDecodeId: state.AudioDecodeNodeId,
            AudioVaeSource: audioVaeSource,
            CurrentOutputMedia: MediaRef.FromWGNodeData(state.CurrentOutputMedia, bridge),
            HasPostDecodeWrappers: state.HasPostDecodeWrappers);
    }
}

/// <summary>
/// Owns the state captured from the original post-video chain and exposes
/// stage-facing artifacts derived from that state.
/// </summary>
internal sealed class LtxPostVideoChainCapture
{
    private readonly LtxStageInputArtifactFactory artifacts;
    private readonly LtxAudioReferenceResolver audioReferences;

    public LtxPostVideoChainState State { get; }

    public WGNodeData CurrentOutputMedia => State.CurrentOutputMedia;
    public JArray AvLatentPath => State.AvLatentPath;
    public JArray DecodeOutputPath => State.DecodeOutputPath;
    public bool HasPostDecodeWrappers => State.HasPostDecodeWrappers;

    private LtxPostVideoChainCapture(
        WorkflowGenerator generator,
        Ltx2ClipAudioReuseState audioReuse,
        LtxPostVideoChainState state)
    {
        State = state;
        audioReferences = new LtxAudioReferenceResolver(generator, audioReuse, state);
        artifacts = new LtxStageInputArtifactFactory(generator, state, audioReferences);
    }

    public static LtxPostVideoChainCapture TryCapture(WorkflowGenerator generator) =>
        TryCaptureCore(generator, audioReuse: null, stage: null);

    public static LtxPostVideoChainCapture TryCapture(
        WorkflowGenerator generator,
        ClipContext clipContext,
        StagePlan stage) =>
        TryCaptureCore(generator, clipContext.AudioReuse, stage);

    private static LtxPostVideoChainCapture TryCaptureCore(
        WorkflowGenerator generator,
        Ltx2ClipAudioReuseState audioReuse,
        StagePlan stage)
    {
        bool useReusedAudio = LtxAudioReuseState.UsesCapturedAudio(stage);
        LtxPostVideoChainState state =
            LtxPostVideoChainInspector.TryCapture(generator, useReusedAudio);
        if (state is null)
        {
            return null;
        }

        LtxAudioReuseState.CompletePostVideoChainCapture(
            audioReuse,
            stage,
            state);

        return new LtxPostVideoChainCapture(generator, audioReuse, state);
    }

    public WGNodeData CreateStageInput() => artifacts.CreateStageInput();

    public WGNodeData CreateStageInputVideoLatent() => artifacts.CreateStageInputVideoLatent();

    public WGNodeData CreateStageInputVae() => artifacts.CreateStageInputVae();

    public bool CanReuseCurrentOutputAsStageInput(WGNodeData sourceMedia) =>
        artifacts.CanReuseCurrentOutputAsStageInput(sourceMedia);

    public WGNodeData CreateDetachedGuideMedia(WGNodeData vae) =>
        artifacts.CreateDetachedGuideMedia(vae);

    public void AttachSourceAudio(WGNodeData media) => audioReferences.AttachSourceAudio(media);
}

internal static class LtxPostVideoChainSplicer
{
    public static void SpliceCurrentOutput(
        LtxPostVideoChainCapture capture,
        WorkflowGenerator generator,
        WGNodeData vae)
    {
        if (generator.CurrentMedia?.Path is not JArray { Count: 2 })
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(generator);
        LtxChainCapture chainCapture =
            LtxPostVideoChainInspector.Rehydrate(capture.State, bridge);
        MediaRef stageOutput = MediaRef.FromWGNodeData(generator.CurrentMedia, bridge);
        MediaRef vaeRef =
            MediaRef.FromWGNodeData(vae, bridge)
            ?? MediaRef.FromWGNodeData(generator.CurrentVae, bridge);
        LtxDecodeConfig decodeConfig = LtxDecodeConfig.From(generator);

        MediaRef result =
            LtxPostChainRebuilder.SpliceCurrentOutput(
                bridge,
                chainCapture,
                stageOutput,
                vaeRef,
                decodeConfig);

        if (result is not null)
        {
            generator.CurrentMedia = result.ToWGNodeData(generator);
            capture.AttachSourceAudio(generator.CurrentMedia);
        }
    }

    public static void SpliceCurrentOutputToDedicatedBranch(
        LtxPostVideoChainCapture capture,
        WorkflowGenerator generator,
        WGNodeData vae,
        int outputWidth,
        int outputHeight,
        int? outputFrames,
        int? outputFps)
    {
        if (generator.CurrentMedia?.Path is not JArray { Count: 2 })
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(generator);
        LtxChainCapture chainCapture =
            LtxPostVideoChainInspector.Rehydrate(capture.State, bridge);
        MediaRef stageOutput = MediaRef.FromWGNodeData(generator.CurrentMedia, bridge);
        MediaRef vaeRef =
            MediaRef.FromWGNodeData(vae, bridge)
            ?? MediaRef.FromWGNodeData(generator.CurrentVae, bridge);
        LtxDecodeConfig decodeConfig = LtxDecodeConfig.From(generator);

        MediaRef result = LtxPostChainRebuilder.SpliceCurrentOutputToDedicatedBranch(
            bridge,
            chainCapture,
            stageOutput,
            vaeRef,
            decodeConfig,
            outputWidth,
            outputHeight,
            outputFrames,
            outputFps);

        if (result is not null)
        {
            generator.CurrentMedia = result.ToWGNodeData(generator);
        }
    }
}
