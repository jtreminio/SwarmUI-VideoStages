using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Selects and attaches the source audio reference that must survive stage rebuilds.
/// </summary>
internal sealed class LtxAudioReferenceResolver
{
    private readonly WorkflowGenerator g;
    private readonly Ltx2ClipAudioReuseState audioReuse;
    private readonly LtxPostVideoChainState state;

    public LtxAudioReferenceResolver(
        WorkflowGenerator generator,
        Ltx2ClipAudioReuseState audioReuse,
        LtxPostVideoChainState state)
    {
        g = generator;
        this.audioReuse = audioReuse;
        this.state = state;
    }

    public void AttachSourceAudio(WGNodeData media)
    {
        if (media is null)
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

    internal WGNodeData CreateSourceAudioReference()
    {
        if (state.UseReusedAudioLatent
            && audioReuse is not null
            && audioReuse.TryGetPath(out JArray reusedAudioLatentPath))
        {
            return new WGNodeData(
                PathUtils.Clone(reusedAudioLatentPath),
                g,
                WGNodeData.DT_LATENT_AUDIO,
                ResolveAudioCompat());
        }

        if (state.CurrentOutputMedia?.AttachedAudio is WGNodeData
            {
                DataType: var attachedType,
                Path: JArray { Count: 2 },
            } preparedAudioLatent
            && attachedType == WGNodeData.DT_LATENT_AUDIO)
        {
            // Clip preparation may have installed a deliberately window-masked latent (audio
            // segments over no locked base, or incoming boundary audio context). It is already the
            // exact sampling input and must outrank the captured root chain's native audio.
            return CloneAudioReference(preparedAudioLatent);
        }

        if (ReferencesCapturedDecodedAudio(state.CurrentOutputMedia?.AttachedAudio))
        {
            // Final publication exposes decoded audio, and that decode can still trace to an
            // originally uploaded track. For another LTX stage, however, the captured separator's
            // audio output is the native handoff; decoding and re-encoding it only adds a second
            // ensure/encode/noise-mask chain.
            return new WGNodeData(
                PathUtils.Clone(state.AudioLatentPath),
                g,
                WGNodeData.DT_LATENT_AUDIO,
                ResolveAudioCompat());
        }

        if (IsExplicitUploadAudio(state.CurrentOutputMedia?.AttachedAudio))
        {
            JArray currentAudioLatentPath = PathUtils.Clone(state.AudioLatentPath);
            if (state.CurrentOutputMedia.AttachedAudio?.Path is JArray { Count: 2 } explicitUploadPath
                && IsAudioLatentDerivedFromUpload(currentAudioLatentPath, $"{explicitUploadPath[0]}"))
            {
                return new WGNodeData(
                    currentAudioLatentPath,
                    g,
                    WGNodeData.DT_LATENT_AUDIO,
                    ResolveAudioCompat());
            }
            return CloneAudioReference(state.CurrentOutputMedia.AttachedAudio);
        }

        return new WGNodeData(
            PathUtils.Clone(state.AudioLatentPath),
            g,
            WGNodeData.DT_LATENT_AUDIO,
            ResolveAudioCompat());
    }

    private bool ReferencesCapturedDecodedAudio(WGNodeData audio)
    {
        if (state.AudioLatentPath is not { Count: 2 }
            || !LtxDecodedAudioHandoff.TryResolveNativeLatent(
                g,
                audio,
                out JArray nativePath))
        {
            return false;
        }

        return JToken.DeepEquals(nativePath, state.AudioLatentPath);
    }

    internal bool IsExplicitUploadAudio(WGNodeData audio)
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
            PathUtils.Clone(audio.Path),
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

    private T2IModelCompatClass ResolveAudioCompat()
    {
        return g.CurrentAudioVae?.Compat
            ?? LtxStageInputArtifactFactory.ResolveVideoCompat(g, state);
    }
}
