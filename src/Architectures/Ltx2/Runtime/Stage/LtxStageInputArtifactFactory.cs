using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxStageInputArtifactFactory
{
    private readonly WorkflowGenerator g;
    private readonly LtxPostVideoChainState state;
    private readonly LtxAudioReferenceResolver audioReferences;

    public LtxStageInputArtifactFactory(
        WorkflowGenerator generator,
        LtxPostVideoChainState state,
        LtxAudioReferenceResolver audioReferences)
    {
        g = generator;
        this.state = state;
        this.audioReferences = audioReferences;
    }

    public WGNodeData CreateStageInput()
    {
        WGNodeData stageInput = WithCurrentMediaDimensions(new WGNodeData(
            state.AvLatentPath,
            g,
            WGNodeData.DT_LATENT_AUDIOVIDEO,
            T2IModelClassSorter.CompatLtxv2));
        audioReferences.AttachSourceAudio(stageInput);
        return stageInput;
    }

    public WGNodeData CreateStageInputVideoLatent()
    {
        WGNodeData videoLatent = WithCurrentMediaDimensions(new WGNodeData(
            ResolveReusableVideoLatentRoute(),
            g,
            WGNodeData.DT_LATENT_VIDEO,
            T2IModelClassSorter.CompatLtxv2));
        audioReferences.AttachSourceAudio(videoLatent);
        return videoLatent;
    }

    public WGNodeData CreateStageInputVae()
    {
        return new WGNodeData(
            state.VideoVaePath?.DeepClone() as JArray,
            g,
            WGNodeData.DT_VAE,
            T2IModelClassSorter.CompatLtxv2);
    }

    public bool CanReuseCurrentOutputAsStageInput(WGNodeData sourceMedia)
    {
        return !state.HasPostDecodeWrappers
            && sourceMedia?.Path is JArray sourcePath
            && JToken.DeepEquals(sourcePath, state.CurrentOutputMedia.Path);
    }

    public WGNodeData CreateDetachedGuideMedia(WGNodeData vae)
    {
        if (vae?.Path is not JArray { Count: 2 } vaePath)
        {
            return CloneCurrentOutputWithAttachedAudio();
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput avLatentSource = bridge.ResolvePath(state.AvLatentPath);
        INodeOutput vaeSource = bridge.ResolvePath(vaePath);
        if (avLatentSource is null || vaeSource is null)
        {
            return CloneCurrentOutputWithAttachedAudio();
        }

        LTXVSeparateAVLatentNode separate =
            bridge.NodeAt<LTXVSeparateAVLatentNode>(state.AudioLatentPath);
        if (separate is null)
        {
            separate = bridge.AddNode(new LTXVSeparateAVLatentNode());
            separate.AvLatent.ConnectToUntyped(avLatentSource);
        }

        string decodeNodeId = LtxPostChainRebuilder.AddDecode(
            bridge,
            vaeSource,
            separate.VideoLatent,
            LtxDecodeConfig.From(g)).Id;

        WGNodeData detachedGuide = WithCurrentMediaDimensions(new WGNodeData(
            new JArray(decodeNodeId, 0),
            g,
            WGNodeData.DT_VIDEO,
            vae.Compat));
        audioReferences.AttachSourceAudio(detachedGuide);
        return detachedGuide;
    }

    internal static WGNodeData CloneMedia(WorkflowGenerator generator, WGNodeData media)
    {
        if (media?.Path is not JArray { Count: 2 } path)
        {
            return null;
        }

        WGNodeData cloned = new(path?.DeepClone() as JArray, generator, media.DataType, media.Compat)
        {
            Width = media.Width,
            Height = media.Height,
            Frames = media.Frames,
            FPS = media.FPS
        };
        if (media.AttachedAudio?.Path is JArray { Count: 2 } audioPath)
        {
            cloned.AttachedAudio = new WGNodeData(
                audioPath?.DeepClone() as JArray,
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

    private JArray ResolveReusableVideoLatentRoute()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.ResolvePath(state.AvLatentPath)?.Node is LTXVConcatAVLatentNode concat
            && concat.VideoLatent.Connection is INodeOutput concatVideo)
        {
            return WorkflowBridge.ToPath(concatVideo);
        }
        return new JArray(state.AudioLatentPath[0], 0);
    }

    private WGNodeData CloneCurrentOutputWithAttachedAudio()
    {
        WGNodeData copy = CloneMedia(g, state.CurrentOutputMedia);
        audioReferences.AttachSourceAudio(copy);
        return copy;
    }

    private WGNodeData WithCurrentMediaDimensions(WGNodeData media)
    {
        media.Width = state.CurrentOutputMedia.Width;
        media.Height = state.CurrentOutputMedia.Height;
        media.Frames = state.CurrentOutputMedia.Frames;
        media.FPS = state.CurrentOutputMedia.FPS;
        return media;
    }
}
