using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;
using VideoStages.Architectures.Ltx2.Runtime.Audio;
using VideoStages.Execution.Graph;

namespace VideoStages.Architectures.Ltx2.Runtime.Chain;

internal sealed class LtxPostVideoChain
{
    private readonly WorkflowGenerator generator;
    private readonly LtxAudioReferenceResolver audioReferences;
    private readonly JArray avLatentPath;
    private readonly JArray audioLatentPath;
    private readonly JArray videoVaePath;
    private readonly JArray audioVaePath;
    private readonly string videoDecodeNodeId;
    private readonly string audioDecodeNodeId;
    private bool isSpliced;

    public WGNodeData CurrentOutputMedia { get; }
    public JArray AvLatentPath => CopyPath(avLatentPath);
    public string VideoDecodeNodeId => videoDecodeNodeId;
    public bool HasPostDecodeWrappers { get; }

    private LtxPostVideoChain(
        WorkflowGenerator generator,
        Ltx2ClipAudioReuseState audioReuse,
        WGNodeData currentOutputMedia,
        JArray avLatentPath,
        JArray audioLatentPath,
        JArray videoVaePath,
        JArray audioVaePath,
        string videoDecodeNodeId,
        string audioDecodeNodeId,
        bool hasPostDecodeWrappers,
        bool useReusedAudioLatent)
    {
        this.generator = generator;
        CurrentOutputMedia = currentOutputMedia;
        this.avLatentPath = CopyPath(avLatentPath);
        this.audioLatentPath = CopyPath(audioLatentPath);
        this.videoVaePath = CopyPath(videoVaePath);
        this.audioVaePath = CopyPath(audioVaePath);
        this.videoDecodeNodeId = videoDecodeNodeId;
        this.audioDecodeNodeId = audioDecodeNodeId;
        HasPostDecodeWrappers = hasPostDecodeWrappers;
        audioReferences = new LtxAudioReferenceResolver(
            generator,
            audioReuse,
            currentOutputMedia,
            this.audioLatentPath,
            useReusedAudioLatent);
    }

    public static LtxPostVideoChain TryCapture(WorkflowGenerator generator) =>
        TryCaptureCore(generator, audioReuse: null, stage: null);

    public static LtxPostVideoChain TryCapture(
        WorkflowGenerator generator,
        ClipContext clipContext,
        StagePlan stage) =>
        TryCaptureCore(generator, clipContext.AudioReuse, stage);

    private static LtxPostVideoChain TryCaptureCore(
        WorkflowGenerator generator,
        Ltx2ClipAudioReuseState audioReuse,
        StagePlan stage)
    {
        bool useReusedAudio = LtxAudioReuseState.UsesCapturedAudio(stage);
        if (generator.CurrentMedia?.IsRawMedia != true
            || generator.CurrentMedia.Path is not JArray { Count: 2 })
        {
            return null;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        MediaRef currentMedia = MediaRef.FromWGNodeData(generator.CurrentMedia, bridge);
        if (currentMedia?.Output?.Node is not ComfyNode mediaNode)
        {
            return null;
        }

        IVaeDecode decode = mediaNode as IVaeDecode
            ?? bridge.Graph.FindNearestUpstream<IVaeDecode>(mediaNode);
        if (decode is null
            || decode.Samples.Connection?.Node is not LTXVSeparateAVLatentNode separate
            || separate.AvLatent.Connection is not INodeOutput avLatentSource
            || decode.Vae.Connection is not INodeOutput videoVaeSource)
        {
            return null;
        }

        LTXVAudioVAEDecodeNode audioDecode = bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>()
            .FirstOrDefault(n =>
                n.Samples.Connection?.Node == separate
                && n.Samples.Connection?.SlotIndex == 1);
        INodeOutput audioVaeSource = audioDecode?.AudioVae.Connection
            ?? MediaRef.FromWGNodeData(generator.CurrentAudioVae, bridge)?.Output;
        if (audioVaeSource is null)
        {
            return null;
        }

        LtxPostVideoChain chain = new(
            generator,
            audioReuse,
            CloneMedia(generator, generator.CurrentMedia),
            WorkflowBridge.ToPath(avLatentSource),
            new JArray(separate.Id, 1),
            WorkflowBridge.ToPath(videoVaeSource),
            WorkflowBridge.ToPath(audioVaeSource),
            decode.Id,
            audioDecode?.Id,
            !ReferenceEquals(currentMedia.Output.Node, decode),
            useReusedAudio);
        LtxAudioReuseState.CompletePostVideoChainCapture(
            audioReuse,
            stage,
            chain.audioLatentPath);

        return chain;
    }

    public WGNodeData CreateStageInput()
    {
        WGNodeData stageInput = WithCurrentMediaDimensions(new WGNodeData(
            CopyPath(avLatentPath),
            generator,
            WGNodeData.DT_LATENT_AUDIOVIDEO,
            T2IModelClassSorter.CompatLtxv2));
        audioReferences.AttachSourceAudio(stageInput);
        return stageInput;
    }

    public WGNodeData CreateStageInputVideoLatent()
    {
        WGNodeData videoLatent = WithCurrentMediaDimensions(new WGNodeData(
            ResolveReusableVideoLatentRoute(),
            generator,
            WGNodeData.DT_LATENT_VIDEO,
            T2IModelClassSorter.CompatLtxv2));
        audioReferences.AttachSourceAudio(videoLatent);
        return videoLatent;
    }

    public WGNodeData CreateStageInputVae() => new(
        CopyPath(videoVaePath),
        generator,
        WGNodeData.DT_VAE,
        T2IModelClassSorter.CompatLtxv2);

    public bool CanReuseCurrentOutputAsStageInput(WGNodeData sourceMedia) =>
        !HasPostDecodeWrappers
        && sourceMedia?.Path is JArray sourcePath
        && JToken.DeepEquals(sourcePath, CurrentOutputMedia.Path);

    public bool ReferencesOutput(WGNodeData media) =>
        media?.Path is JArray mediaPath
        && (JToken.DeepEquals(mediaPath, CurrentOutputMedia?.Path)
            || JToken.DeepEquals(mediaPath, new JArray(videoDecodeNodeId, 0)));

    public WGNodeData CreateDetachedGuideMedia(WGNodeData vae)
    {
        if (vae?.Path is not JArray { Count: 2 } vaePath)
        {
            return CloneCurrentOutputWithAttachedAudio();
        }

        using WorkflowBridge bridge = BridgeSync.For(generator);
        INodeOutput avLatentSource = bridge.ResolvePath(avLatentPath);
        INodeOutput vaeSource = bridge.ResolvePath(vaePath);
        if (avLatentSource is null || vaeSource is null)
        {
            return CloneCurrentOutputWithAttachedAudio();
        }

        LTXVSeparateAVLatentNode separate =
            bridge.NodeAt<LTXVSeparateAVLatentNode>(audioLatentPath);
        if (separate is null)
        {
            separate = bridge.AddNode(new LTXVSeparateAVLatentNode());
            separate.AvLatent.ConnectToUntyped(avLatentSource);
        }

        string decodeNodeId = LtxPostChainRebuilder.AddDecode(
            bridge,
            vaeSource,
            separate.VideoLatent,
            LtxDecodeConfig.From(generator)).Id;

        WGNodeData detachedGuide = WithCurrentMediaDimensions(new WGNodeData(
            new JArray(decodeNodeId, 0),
            generator,
            WGNodeData.DT_VIDEO,
            vae.Compat));
        audioReferences.AttachSourceAudio(detachedGuide);
        return detachedGuide;
    }

    public void AttachSourceAudio(WGNodeData media) => audioReferences.AttachSourceAudio(media);

    public void SpliceCurrentOutput(WGNodeData vae)
    {
        if (isSpliced || generator.CurrentMedia?.Path is not JArray { Count: 2 })
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(generator);
        ComfyNode audioDecode = audioDecodeNodeId is null
            ? null
            : bridge.Graph.GetNode(audioDecodeNodeId);
        if (audioDecodeNodeId is not null && audioDecode is null)
        {
            return;
        }
        MediaRef captured = MediaRef.FromWGNodeData(CurrentOutputMedia, bridge);
        MediaRef stageOutput = MediaRef.FromWGNodeData(generator.CurrentMedia, bridge);
        MediaRef spliceVae = MediaRef.FromWGNodeData(vae, bridge)
            ?? MediaRef.FromWGNodeData(generator.CurrentVae, bridge);
        ComfyNode decode = bridge.Graph.GetNode(videoDecodeNodeId);
        LTXVSeparateAVLatentNode oldSeparate =
            bridge.Graph.GetNode<LTXVSeparateAVLatentNode>($"{audioLatentPath[0]}");
        if (captured is null
            || stageOutput is null
            || spliceVae is null
            || oldSeparate is null
            || decode is not IVaeDecode vaeDecode
            || decode.Outputs.Count == 0
            || vaeDecode.Samples.Connection?.Node?.Id != oldSeparate.Id
            || (audioDecode is not null
                && audioDecode.FindInput("samples")?.Connection?.Node?.Id != oldSeparate.Id))
        {
            return;
        }

        LTXVSeparateAVLatentNode newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectToUntyped(stageOutput.Output);
        ReplaceVideoDecode(bridge, decode, spliceVae.Output, newSeparate);
        if (audioDecode is not null)
        {
            bridge.Graph.RetargetConnections(
                oldSeparate.AudioLatent,
                newSeparate.AudioLatent,
                (node, input) => node.Id == audioDecode.Id && input.Name == "samples");
        }

        generator.CurrentMedia = captured.ToWGNodeData(generator);
        AttachSourceAudio(generator.CurrentMedia);
        isSpliced = true;
    }

    private void ReplaceVideoDecode(
        WorkflowBridge bridge,
        ComfyNode oldDecode,
        INodeOutput vaeOutput,
        LTXVSeparateAVLatentNode newSeparate)
    {
        INodeOutput oldImageOutput = oldDecode.Outputs[0];
        // The id survives so paths already handed out still resolve; its samples do not.
        VideoGraphHelpers.RemoveNode(generator, bridge, oldDecode.Id);

        ComfyNode newDecode = LtxPostChainRebuilder.AddDecode(
            bridge,
            vaeOutput,
            newSeparate.VideoLatent,
            LtxDecodeConfig.From(generator),
            preserveId: oldDecode.Id);

        bridge.Graph.RetargetConnections(oldImageOutput, newDecode.Outputs[0]);
    }

    public void SpliceCurrentOutputToDedicatedBranch(
        WGNodeData vae,
        int outputWidth,
        int outputHeight,
        int? outputFrames,
        int? outputFps)
    {
        if (isSpliced || generator.CurrentMedia?.Path is not JArray { Count: 2 })
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(generator);
        MediaRef captured = MediaRef.FromWGNodeData(CurrentOutputMedia, bridge);
        MediaRef stageOutput = MediaRef.FromWGNodeData(generator.CurrentMedia, bridge);
        MediaRef spliceVae = MediaRef.FromWGNodeData(vae, bridge)
            ?? MediaRef.FromWGNodeData(generator.CurrentVae, bridge);
        INodeOutput audioVaeSource = bridge.ResolvePath(audioVaePath);
        if (captured is null
            || stageOutput is null
            || spliceVae is null
            || audioVaeSource is null)
        {
            return;
        }

        LTXVSeparateAVLatentNode newSeparate = bridge.AddNode(new LTXVSeparateAVLatentNode());
        newSeparate.AvLatent.ConnectToUntyped(stageOutput.Output);
        ComfyNode dedicatedDecode = LtxPostChainRebuilder.AddDecode(
            bridge,
            spliceVae.Output,
            newSeparate.VideoLatent,
            LtxDecodeConfig.From(generator));

        MediaRef result = new()
        {
            Output = dedicatedDecode.Outputs[0],
            DataType = WGNodeData.DT_VIDEO,
            Compat = spliceVae.Compat ?? captured.Compat,
            Width = outputWidth,
            Height = outputHeight,
            Frames = outputFrames ?? captured.Frames,
            FPS = outputFps ?? captured.FPS,
        };
        LtxPostChainRebuilder.AttachDecodedLtxAudio(
            bridge,
            result,
            new MediaRef
            {
                Output = audioVaeSource,
                DataType = WGNodeData.DT_AUDIOVAE,
                Compat = captured.Compat
            });

        generator.CurrentMedia = result.ToWGNodeData(generator);
        isSpliced = true;
    }

    private static WGNodeData CloneMedia(
        WorkflowGenerator generator,
        WGNodeData media)
    {
        if (media?.Path is not JArray { Count: 2 } path)
        {
            return null;
        }

        WGNodeData cloned = new(path.DeepClone() as JArray, generator, media.DataType, media.Compat)
        {
            Width = media.Width,
            Height = media.Height,
            Frames = media.Frames,
            FPS = media.FPS,
        };
        if (media.AttachedAudio?.Path is JArray { Count: 2 } audioPath)
        {
            cloned.AttachedAudio = new WGNodeData(
                audioPath.DeepClone() as JArray,
                generator,
                media.AttachedAudio.DataType,
                media.AttachedAudio.Compat)
            {
                Width = media.AttachedAudio.Width,
                Height = media.AttachedAudio.Height,
                Frames = media.AttachedAudio.Frames,
                FPS = media.AttachedAudio.FPS,
            };
        }
        return cloned;
    }

    private JArray ResolveReusableVideoLatentRoute()
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        if (bridge.ResolvePath(avLatentPath)?.Node is LTXVConcatAVLatentNode concat
            && concat.VideoLatent.Connection is INodeOutput concatVideo)
        {
            return WorkflowBridge.ToPath(concatVideo);
        }
        return new JArray(audioLatentPath[0], 0);
    }

    private WGNodeData CloneCurrentOutputWithAttachedAudio()
    {
        WGNodeData copy = CloneMedia(generator, CurrentOutputMedia);
        audioReferences.AttachSourceAudio(copy);
        return copy;
    }

    private WGNodeData WithCurrentMediaDimensions(WGNodeData media)
    {
        media.Width = CurrentOutputMedia.Width;
        media.Height = CurrentOutputMedia.Height;
        media.Frames = CurrentOutputMedia.Frames;
        media.FPS = CurrentOutputMedia.FPS;
        return media;
    }

    private static JArray CopyPath(JArray path) => path?.DeepClone() as JArray;
}
