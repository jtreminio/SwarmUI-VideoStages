using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.LTX2;

internal sealed class LtxPostVideoChainCapture
{
    private readonly WorkflowGenerator g;
    private readonly ClipAudioState audioReuse;
    public LtxPostVideoChainState State { get; }

    public WGNodeData CurrentOutputMedia => State.CurrentOutputMedia;
    public JArray AvLatentPath => State.AvLatentPath;
    public JArray DecodeOutputPath => State.DecodeOutputPath;
    public bool HasPostDecodeWrappers => State.HasPostDecodeWrappers;

    private LtxPostVideoChainCapture(
        WorkflowGenerator generator,
        ClipAudioState audioReuse,
        LtxPostVideoChainState state)
    {
        g = generator;
        this.audioReuse = audioReuse;
        State = state;
    }

    public static LtxPostVideoChainCapture TryCapture(WorkflowGenerator generator) =>
        TryCaptureCore(generator, audioReuse: null, clip: null, stage: null, mutateReuseAudioState: false);

    public static LtxPostVideoChainCapture TryCapture(
        WorkflowGenerator generator,
        ClipContext clipContext,
        StageSpec stage) =>
        TryCaptureCore(generator, clipContext.AudioReuse, clipContext.Clip, stage, mutateReuseAudioState: true);

    private static LtxPostVideoChainCapture TryCaptureCore(
        WorkflowGenerator generator,
        ClipAudioState audioReuse,
        ClipSpec clip,
        StageSpec stage,
        bool mutateReuseAudioState)
    {
        bool clipCanReuseAudio = clip?.ReuseAudio == true && clip.Stages.Count >= 3;
        bool useReusedAudio = clipCanReuseAudio && stage.ClipStageIndex > 0;
        bool captureReusableAudio = clipCanReuseAudio && stage.ClipStageIndex == 1;
        if (mutateReuseAudioState && !useReusedAudio && !captureReusableAudio)
        {
            audioReuse.Clear();
        }

        if (generator.CurrentMedia?.IsRawMedia != true
            || generator.CurrentMedia.Path is not JArray { Count: 2 })
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
            audioReuse.Remember(new JArray(capture.SeparateId, 1));
        }

        LtxPostVideoChainState state = new(
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
        return new LtxPostVideoChainCapture(generator, audioReuse, state);
    }

    public WGNodeData CreateStageInput()
    {
        WGNodeData stageInput = new(State.AvLatentPath, g, WGNodeData.DT_LATENT_AUDIOVIDEO, ResolveVideoCompat())
        {
            Width = State.CurrentOutputMedia.Width,
            Height = State.CurrentOutputMedia.Height,
            Frames = State.CurrentOutputMedia.Frames,
            FPS = State.CurrentOutputMedia.FPS
        };
        AttachSourceAudio(stageInput);
        return stageInput;
    }

    public WGNodeData CreateStageInputVae()
    {
        return new WGNodeData(
            new JArray(State.VideoVaePath[0], State.VideoVaePath[1]),
            g,
            WGNodeData.DT_VAE,
            ResolveVideoCompat());
    }

    public bool CanReuseCurrentOutputAsStageInput(WGNodeData sourceMedia)
    {
        return !State.HasPostDecodeWrappers
            && sourceMedia?.Path is JArray sourcePath
            && JToken.DeepEquals(sourcePath, State.CurrentOutputMedia.Path);
    }

    public WGNodeData CreateDetachedGuideMedia(WGNodeData vae)
    {
        if (vae?.Path is not JArray { Count: 2 } vaePath)
        {
            return CloneCurrentOutputWithAttachedAudio();
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput avLatentSource = bridge.ResolvePath(State.AvLatentPath);
        INodeOutput vaeSource = bridge.ResolvePath(vaePath);
        if (avLatentSource is null || vaeSource is null)
        {
            return CloneCurrentOutputWithAttachedAudio();
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
            Width = State.CurrentOutputMedia.Width,
            Height = State.CurrentOutputMedia.Height,
            Frames = State.CurrentOutputMedia.Frames,
            FPS = State.CurrentOutputMedia.FPS
        };
        AttachSourceAudio(detachedGuide);
        return detachedGuide;
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

    internal LtxChainCapture BuildChainCapture(WorkflowBridge bridge)
    {
        INodeOutput audioVaeSource = State.AudioVaePath is JArray { Count: 2 } avp
            ? bridge.ResolvePath(avp)
            : null;

        return new LtxChainCapture(
            DecodeId: State.VideoDecodeNodeId,
            SeparateId: $"{State.AudioLatentPath[0]}",
            AudioDecodeId: State.AudioDecodeNodeId,
            AudioVaeSource: audioVaeSource,
            CurrentOutputMedia: MediaRef.FromWGNodeData(State.CurrentOutputMedia, bridge),
            HasPostDecodeWrappers: State.HasPostDecodeWrappers,
            UseReusedAudio: State.UseReusedAudioLatent);
    }

    internal LtxChainOps.DecodeConfig BuildDecodeConfig()
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
        VAEDecodeTiledNode decode = bridge.AddNode(new VAEDecodeTiledNode().With(
            TileSize: g.UserInput.Get(T2IParamTypes.VAETileSize, 768),
            Overlap: g.UserInput.Get(T2IParamTypes.VAETileOverlap, 64),
            TemporalSize: g.UserInput.Get(T2IParamTypes.VAETemporalTileSize, 4096),
            TemporalOverlap: g.UserInput.Get(T2IParamTypes.VAETemporalTileOverlap, 4)));
        decode.Vae.ConnectToUntyped(vaeSource);
        decode.Samples.ConnectToUntyped(samplesSource);
        bridge.SyncNode(decode);
        return decode.Id;
    }

    private WGNodeData CloneCurrentOutputWithAttachedAudio()
    {
        WGNodeData copy = CloneMedia(g, State.CurrentOutputMedia);
        AttachSourceAudio(copy);
        return copy;
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
        if (State.UseReusedAudioLatent
            && audioReuse is not null
            && audioReuse.TryGetPath(out JArray reusedAudioLatentPath))
        {
            return new WGNodeData(
                new JArray(reusedAudioLatentPath[0], reusedAudioLatentPath[1]),
                g,
                WGNodeData.DT_LATENT_AUDIO,
                ResolveAudioCompat());
        }

        if (IsExplicitUploadAudio(State.CurrentOutputMedia?.AttachedAudio))
        {
            JArray currentAudioLatentPath = new(State.AudioLatentPath[0], State.AudioLatentPath[1]);
            if (State.CurrentOutputMedia.AttachedAudio?.Path is JArray { Count: 2 } explicitUploadPath
                && IsAudioLatentDerivedFromUpload(currentAudioLatentPath, $"{explicitUploadPath[0]}"))
            {
                return new WGNodeData(
                    currentAudioLatentPath,
                    g,
                    WGNodeData.DT_LATENT_AUDIO,
                    ResolveAudioCompat());
            }
            return CloneAudioReference(State.CurrentOutputMedia.AttachedAudio);
        }

        return new WGNodeData(
            new JArray(State.AudioLatentPath[0], State.AudioLatentPath[1]),
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

    private bool ShouldUseTiledVaeDecode()
    {
        return g.UserInput.TryGet(T2IParamTypes.VAETileSize, out _);
    }

    private T2IModelCompatClass ResolveVideoCompat()
    {
        return T2IModelClassSorter.CompatLtxv2
            ?? g.CurrentVae?.Compat
            ?? State.CurrentOutputMedia?.Compat
            ?? g.CurrentCompat();
    }

    private T2IModelCompatClass ResolveAudioCompat()
    {
        return g.CurrentAudioVae?.Compat ?? ResolveVideoCompat();
    }
}
