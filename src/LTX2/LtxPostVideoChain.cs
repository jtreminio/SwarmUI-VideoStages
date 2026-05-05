using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.LTX2;

internal sealed class LtxPostVideoChain
{
    private readonly WorkflowGenerator g;
    private readonly bool useReusedAudioLatent;
    public readonly WGNodeData CurrentOutputMedia;
    public readonly JArray AvLatentPath;
    public readonly JArray AudioLatentPath;
    public readonly JArray VideoVaePath;
    public readonly JArray AudioVaePath;
    public readonly string VideoDecodeNodeId;
    public readonly string AudioDecodeNodeId;
    public readonly JArray DecodeOutputPath;
    public readonly bool HasPostDecodeWrappers;

    private LtxPostVideoChain(
        WorkflowGenerator generator,
        WGNodeData currentOutputMedia,
        JArray avLatentPath,
        JArray audioLatentPath,
        JArray videoVaePath,
        JArray audioVaePath,
        string videoDecodeNodeId,
        string audioDecodeNodeId,
        JArray decodeOutputPath,
        bool hasPostDecodeWrappers,
        bool useReusedAudio)
    {
        g = generator;
        useReusedAudioLatent = useReusedAudio;
        CurrentOutputMedia = currentOutputMedia;
        AvLatentPath = avLatentPath;
        AudioLatentPath = audioLatentPath;
        VideoVaePath = videoVaePath;
        AudioVaePath = audioVaePath;
        VideoDecodeNodeId = videoDecodeNodeId;
        AudioDecodeNodeId = audioDecodeNodeId;
        DecodeOutputPath = decodeOutputPath;
        HasPostDecodeWrappers = hasPostDecodeWrappers;
    }

    public static LtxPostVideoChain TryCapture(WorkflowGenerator generator) =>
        TryCapture(generator, null, mutateReuseAudioState: false);

    public static LtxPostVideoChain TryCapture(WorkflowGenerator generator, JsonParser.StageSpec stage) =>
        TryCapture(generator, stage, mutateReuseAudioState: true);

    private static LtxPostVideoChain TryCapture(
        WorkflowGenerator generator,
        JsonParser.StageSpec stage,
        bool mutateReuseAudioState)
    {
        bool clipCanReuseAudio = stage?.ClipReuseAudio == true && stage.ClipStageCount >= 3;
        bool useReusedAudio = clipCanReuseAudio && stage.ClipStageIndex > 0;
        bool captureReusableAudio = clipCanReuseAudio && stage.ClipStageIndex == 1;
        if (mutateReuseAudioState && !useReusedAudio && !captureReusableAudio)
        {
            LtxAudioReuseState.Clear(generator);
        }

        if (generator.CurrentMedia?.IsRawMedia != true
            || generator.CurrentMedia.Path is not JArray { Count: 2 } mediaPath)
        {
            return null;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        MediaRef currentMedia = MediaRef.FromWGNodeData(generator.CurrentMedia, bridge);
        MediaRef currentAudioVae = MediaRef.FromWGNodeData(generator.CurrentAudioVae, bridge);
        LtxChainCapture capture = LtxChainOps.TryCapture(bridge, currentMedia, currentAudioVae, useReusedAudio);
        if (capture is null)
        {
            return null;
        }

        LTXVSeparateAVLatentNode separate = bridge.Graph.GetNode<LTXVSeparateAVLatentNode>(capture.SeparateId);
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

        if (mutateReuseAudioState && captureReusableAudio)
        {
            LtxAudioReuseState.Remember(generator, new JArray(capture.SeparateId, 1));
        }

        return new LtxPostVideoChain(
            generator,
            CloneMedia(generator, generator.CurrentMedia),
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

    public WGNodeData CreateStageInput()
    {
        WGNodeData stageInput = new(AvLatentPath, g, WGNodeData.DT_LATENT_AUDIOVIDEO, ResolveVideoCompat())
        {
            Width = CurrentOutputMedia.Width,
            Height = CurrentOutputMedia.Height,
            Frames = CurrentOutputMedia.Frames,
            FPS = CurrentOutputMedia.FPS
        };
        AttachSourceAudio(stageInput);
        return stageInput;
    }

    public WGNodeData CreateStageInputVae()
    {
        return new WGNodeData(new JArray(VideoVaePath[0], VideoVaePath[1]), g, WGNodeData.DT_VAE, ResolveVideoCompat());
    }

    public bool CanReuseCurrentOutputAsStageInput(WGNodeData sourceMedia)
    {
        return !HasPostDecodeWrappers
            && sourceMedia?.Path is JArray sourcePath
            && JToken.DeepEquals(sourcePath, CurrentOutputMedia.Path);
    }

    public WGNodeData CreateDetachedGuideMedia(WGNodeData vae)
    {
        if (vae?.Path is not JArray { Count: 2 } vaePath)
        {
            WGNodeData detachedCurrent = CloneMedia(g, CurrentOutputMedia);
            AttachSourceAudio(detachedCurrent);
            return detachedCurrent;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput avLatentSource = bridge.ResolvePath(AvLatentPath);
        INodeOutput vaeSource = bridge.ResolvePath(vaePath);
        if (avLatentSource is null || vaeSource is null)
        {
            WGNodeData detachedCurrent = CloneMedia(g, CurrentOutputMedia);
            AttachSourceAudio(detachedCurrent);
            return detachedCurrent;
        }

        LTXVSeparateAVLatentNode detachedSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        detachedSeparate.AvLatent.ConnectToUntyped(avLatentSource);
        bridge.SyncNode(detachedSeparate);

        string decodeNodeId = ShouldUseTiledVaeDecode()
            ? AddTiledVideoDecode(bridge, vaeSource, detachedSeparate.VideoLatent)
            : AddPlainVideoDecode(bridge, vaeSource, detachedSeparate.VideoLatent);
        BridgeSync.SyncLastId(g);

        WGNodeData detachedGuide = new(new JArray(decodeNodeId, 0), g, WGNodeData.DT_VIDEO, vae.Compat)
        {
            Width = CurrentOutputMedia.Width,
            Height = CurrentOutputMedia.Height,
            Frames = CurrentOutputMedia.Frames,
            FPS = CurrentOutputMedia.FPS
        };
        AttachSourceAudio(detachedGuide);
        return detachedGuide;
    }

    private string AddPlainVideoDecode(WorkflowBridge bridge, INodeOutput vaeSource, INodeOutput samplesSource)
    {
        VAEDecodeNode decode = bridge.AddNode(new VAEDecodeNode());
        decode.Vae.ConnectToUntyped(vaeSource);
        decode.Samples.ConnectToUntyped(samplesSource);
        bridge.SyncNode(decode);
        return decode.Id;
    }

    private string AddTiledVideoDecode(WorkflowBridge bridge, INodeOutput vaeSource, INodeOutput samplesSource)
    {
        VAEDecodeTiledNode decode = bridge.AddNode(new VAEDecodeTiledNode());
        decode.Vae.ConnectToUntyped(vaeSource);
        decode.Samples.ConnectToUntyped(samplesSource);
        decode.TileSize.Set(g.UserInput.Get(T2IParamTypes.VAETileSize, 768));
        decode.Overlap.Set(g.UserInput.Get(T2IParamTypes.VAETileOverlap, 64));
        decode.TemporalSize.Set(g.UserInput.Get(T2IParamTypes.VAETemporalTileSize, 4096));
        decode.TemporalOverlap.Set(g.UserInput.Get(T2IParamTypes.VAETemporalTileOverlap, 4));
        bridge.SyncNode(decode);
        return decode.Id;
    }

    public void AttachSourceAudio(WGNodeData media)
    {
        if (media is null)
        {
            return;
        }

        if (IsExplicitUploadAudio(media.AttachedAudio))
        {
            return;
        }

        WGNodeData sourceAudio = CreateSourceAudioReference();
        if (sourceAudio is null)
        {
            return;
        }

        if (media.AttachedAudio?.Path is JArray { Count: 2 } existingAudioPath
            && media.AttachedAudio.DataType == sourceAudio.DataType
            && JToken.DeepEquals(existingAudioPath, sourceAudio.Path))
        {
            return;
        }

        media.AttachedAudio = sourceAudio;
    }

    public void SpliceCurrentOutput(WGNodeData vae)
    {
        if (g.CurrentMedia?.Path is not JArray { Count: 2 } stageOutputPath)
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LtxChainCapture capture = BuildCapture(bridge);
        MediaRef stageOutput = MediaRef.FromWGNodeData(g.CurrentMedia, bridge);
        MediaRef vaeRef = MediaRef.FromWGNodeData(vae, bridge)
                       ?? MediaRef.FromWGNodeData(g.CurrentVae, bridge);
        LtxChainOps.DecodeConfig decodeConfig = BuildDecodeConfig();

        MediaRef result = LtxChainOps.SpliceCurrentOutput(bridge, capture, stageOutput, vaeRef, decodeConfig);
        BridgeSync.SyncLastId(g);

        if (result is not null)
        {
            g.CurrentMedia = result.ToWGNodeData(g);
        }
        AttachSourceAudio(g.CurrentMedia);
    }

    public void SpliceCurrentOutputToDedicatedBranch(
        WGNodeData vae,
        int outputWidth,
        int outputHeight,
        int? outputFrames,
        int? outputFps)
    {
        if (g.CurrentMedia?.Path is not JArray { Count: 2 } stageOutputPath)
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LtxChainCapture capture = BuildCapture(bridge);
        MediaRef stageOutput = MediaRef.FromWGNodeData(g.CurrentMedia, bridge);
        MediaRef vaeRef = MediaRef.FromWGNodeData(vae, bridge)
                       ?? MediaRef.FromWGNodeData(g.CurrentVae, bridge);
        LtxChainOps.DecodeConfig decodeConfig = BuildDecodeConfig();

        MediaRef result = LtxChainOps.SpliceCurrentOutputToDedicatedBranch(
            bridge, capture, stageOutput, vaeRef, decodeConfig,
            outputWidth, outputHeight, outputFrames, outputFps);
        BridgeSync.SyncLastId(g);

        if (result is not null)
        {
            g.CurrentMedia = result.ToWGNodeData(g);
        }
    }

    public void RetargetAnimationSaves(JArray newImagePath)
    {
        if (newImagePath is not { Count: 2 })
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput oldOutput = bridge.ResolvePath(CurrentOutputMedia.Path);
        INodeOutput newOutput = bridge.ResolvePath(newImagePath);
        if (oldOutput is null || newOutput is null)
        {
            return;
        }

        LtxChainOps.RetargetAnimationSaves(bridge, oldOutput, newOutput);
        BridgeSync.SyncLastId(g);
    }

    private static WGNodeData CloneMedia(WorkflowGenerator generator, WGNodeData media)
    {
        if (media?.Path is not JArray { Count: 2 } path)
        {
            return null;
        }

        WGNodeData cloned = new(new JArray(path[0], path[1]), generator, media.DataType, media.Compat)
        {
            Width = media.Width,
            Height = media.Height,
            Frames = media.Frames,
            FPS = media.FPS
        };
        if (media.AttachedAudio?.Path is JArray { Count: 2 } audioPath)
        {
            cloned.AttachedAudio = new WGNodeData(
                new JArray(audioPath[0], audioPath[1]),
                generator,
                media.AttachedAudio.DataType,
                media.AttachedAudio.Compat)
            {
                Width = media.AttachedAudio.Width,
                Height = media.AttachedAudio.Height,
                Frames = media.AttachedAudio.Frames,
                FPS = media.AttachedAudio.FPS
            };
        }
        return cloned;
    }

    private WGNodeData CreateSourceAudioReference()
    {
        if (useReusedAudioLatent && LtxAudioReuseState.TryGetPath(g, out JArray reusedAudioLatentPath))
        {
            return new WGNodeData(
                reusedAudioLatentPath,
                g,
                WGNodeData.DT_LATENT_AUDIO,
                ResolveAudioCompat());
        }

        if (IsExplicitUploadAudio(CurrentOutputMedia?.AttachedAudio))
        {
            JArray currentAudioLatentPath = new(AudioLatentPath[0], AudioLatentPath[1]);
            if (CurrentOutputMedia.AttachedAudio?.Path is JArray { Count: 2 } explicitUploadPath
                && IsAudioLatentDerivedFromUpload(currentAudioLatentPath, $"{explicitUploadPath[0]}"))
            {
                return new WGNodeData(
                    currentAudioLatentPath,
                    g,
                    WGNodeData.DT_LATENT_AUDIO,
                    ResolveAudioCompat());
            }
            return CloneAudioReference(CurrentOutputMedia.AttachedAudio);
        }

        return new WGNodeData(
            new JArray(AudioLatentPath[0], AudioLatentPath[1]),
            g,
            WGNodeData.DT_LATENT_AUDIO,
            ResolveAudioCompat());
    }

    private bool IsAudioLatentDerivedFromUpload(JArray audioLatentPath, string uploadNodeId)
    {
        if (audioLatentPath is not { Count: 2 }
            || string.IsNullOrWhiteSpace(uploadNodeId))
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput latentOutput = bridge.ResolvePath(audioLatentPath);
        if (latentOutput?.Node is not ComfyNode latentNode)
        {
            return false;
        }

        return bridge.Graph.IsReachableUpstream(latentNode, uploadNodeId);
    }

    private WGNodeData CloneAudioReference(WGNodeData audio)
    {
        return new WGNodeData(
            new JArray(audio.Path[0], audio.Path[1]),
            g,
            audio.DataType,
            audio.Compat)
        {
            Width = audio.Width,
            Height = audio.Height,
            Frames = audio.Frames,
            FPS = audio.FPS
        };
    }

    private bool IsExplicitUploadAudio(WGNodeData audio)
    {
        if (audio?.DataType != WGNodeData.DT_AUDIO
            || audio.Path is not JArray { Count: 2 } audioPath)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput audioOutput = bridge.ResolvePath(audioPath);
        if (audioOutput?.Node is not ComfyNode audioNode)
        {
            return false;
        }

        return audioNode is SwarmLoadAudioB64Node
            || bridge.Graph.FindNearestUpstream<SwarmLoadAudioB64Node>(audioNode) is not null;
    }

    private LtxChainCapture BuildCapture(WorkflowBridge bridge)
    {
        INodeOutput audioVaeSource = AudioVaePath is JArray { Count: 2 } avp
            ? bridge.ResolvePath(avp)
            : null;

        return new LtxChainCapture(
            DecodeId: VideoDecodeNodeId,
            SeparateId: $"{AudioLatentPath[0]}",
            AudioDecodeId: AudioDecodeNodeId,
            AudioVaeSource: audioVaeSource,
            CurrentOutputMedia: MediaRef.FromWGNodeData(CurrentOutputMedia, bridge),
            HasPostDecodeWrappers: HasPostDecodeWrappers,
            UseReusedAudio: useReusedAudioLatent);
    }

    private LtxChainOps.DecodeConfig BuildDecodeConfig()
    {
        if (!ShouldUseTiledVaeDecode())
        {
            return new LtxChainOps.DecodeConfig(false);
        }

        return new LtxChainOps.DecodeConfig(
            UseTiledDecode: true,
            TileSize: g.UserInput.Get(T2IParamTypes.VAETileSize, 768),
            Overlap: g.UserInput.Get(T2IParamTypes.VAETileOverlap, 64),
            TemporalSize: g.UserInput.Get(T2IParamTypes.VAETemporalTileSize, 4096),
            TemporalOverlap: g.UserInput.Get(T2IParamTypes.VAETemporalTileOverlap, 4));
    }

    private bool ShouldUseTiledVaeDecode()
    {
        return g.UserInput.TryGet(T2IParamTypes.VAETileSize, out _);
    }

    private T2IModelCompatClass ResolveVideoCompat()
    {
        return T2IModelClassSorter.CompatLtxv2
            ?? g.CurrentVae?.Compat
            ?? CurrentOutputMedia?.Compat
            ?? g.CurrentCompat();
    }

    private T2IModelCompatClass ResolveAudioCompat()
    {
        return g.CurrentAudioVae?.Compat ?? ResolveVideoCompat();
    }
}
