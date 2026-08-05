using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxAudioReferenceResolver
{
    private readonly WorkflowGenerator g;
    private readonly Ltx2ClipAudioReuseState audioReuse;
    private readonly WGNodeData currentOutputMedia;
    private readonly JArray audioLatentPath;
    private readonly bool useReusedAudioLatent;

    public LtxAudioReferenceResolver(
        WorkflowGenerator generator,
        Ltx2ClipAudioReuseState audioReuse,
        WGNodeData currentOutputMedia,
        JArray audioLatentPath,
        bool useReusedAudioLatent)
    {
        g = generator;
        this.audioReuse = audioReuse;
        this.currentOutputMedia = currentOutputMedia;
        this.audioLatentPath = audioLatentPath?.DeepClone() as JArray;
        this.useReusedAudioLatent = useReusedAudioLatent;
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
        if (useReusedAudioLatent
            && audioReuse is not null
            && audioReuse.TryGetPath(out JArray reusedAudioLatentPath))
        {
            return new WGNodeData(
                reusedAudioLatentPath?.DeepClone() as JArray,
                g,
                WGNodeData.DT_LATENT_AUDIO,
                ResolveAudioCompat());
        }

        if (currentOutputMedia?.AttachedAudio is WGNodeData
            {
                DataType: var attachedType,
                Path: JArray { Count: 2 },
            } preparedAudioLatent
            && attachedType == WGNodeData.DT_LATENT_AUDIO)
        {
            // Reuse the prepared latent to preserve windowing and boundary context.
            return CloneAudioReference(preparedAudioLatent);
        }

        if (ReferencesCapturedDecodedAudio(currentOutputMedia?.AttachedAudio))
        {
            // Reuse the separator's native audio latent instead of decoding and re-encoding it.
            return new WGNodeData(
                audioLatentPath?.DeepClone() as JArray,
                g,
                WGNodeData.DT_LATENT_AUDIO,
                ResolveAudioCompat());
        }

        if (IsExplicitUploadAudio(currentOutputMedia?.AttachedAudio))
        {
            JArray currentAudioLatentPath = audioLatentPath?.DeepClone() as JArray;
            if (currentOutputMedia.AttachedAudio?.Path is JArray { Count: 2 } explicitUploadPath
                && IsAudioLatentDerivedFromUpload(currentAudioLatentPath, $"{explicitUploadPath[0]}"))
            {
                return new WGNodeData(
                    currentAudioLatentPath,
                    g,
                    WGNodeData.DT_LATENT_AUDIO,
                    ResolveAudioCompat());
            }
            return CloneAudioReference(currentOutputMedia.AttachedAudio);
        }

        return new WGNodeData(
            audioLatentPath?.DeepClone() as JArray,
            g,
            WGNodeData.DT_LATENT_AUDIO,
            ResolveAudioCompat());
    }

    private bool ReferencesCapturedDecodedAudio(WGNodeData audio)
    {
        if (audioLatentPath is not { Count: 2 }
            || !LtxDecodedAudioHandoff.TryResolveNativeLatent(
                g,
                audio,
                out JArray nativePath))
        {
            return false;
        }

        return JToken.DeepEquals(nativePath, audioLatentPath);
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
            audio.Path?.DeepClone() as JArray,
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
            ?? T2IModelClassSorter.CompatLtxv2;
    }
}
