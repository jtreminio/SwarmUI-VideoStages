using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;

namespace VideoStages.LTX2;

internal sealed record ResolvedClipRef(WGNodeData Image, ImageRefSpec Spec, double Strength);

internal sealed class LtxStageExecutor(
    WorkflowGenerator g,
    RootVideoStageHandoff rootVideoStageHandoff,
    RootVideoStageResizer rootVideoStageResizer)
{
    private const int ImgCompression = 18;
    private const double DefaultGuideMergeStrength = 1.0;
    private const int DefaultFps = 24;
    private const int DefaultFrameCount = 97;
    private const double DefaultCfg = 3;
    private const string DefaultSampler = "euler";
    private const string DefaultScheduler = "normal";

    internal const int DefaultFpsValue = DefaultFps;
    internal const int DefaultFrameCountValue = DefaultFrameCount;
    internal const double DefaultCfgValue = DefaultCfg;
    internal const string DefaultSamplerValue = DefaultSampler;
    internal const string DefaultSchedulerValue = DefaultScheduler;

    public void RunStage(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        bool skipGuideReinjection,
        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent,
        LtxPostVideoChainCapture postVideoChain,
        IReadOnlyList<ResolvedClipRef> clipRefs = null,
        double guideMergeStrength = DefaultGuideMergeStrength)
    {
        postVideoChain?.AttachSourceAudio(sourceMedia);
        g.IsImageToVideo = true;

        try
        {
            foreach (
                Action<WorkflowGenerator.ImageToVideoGenInfo> handler in
                WorkflowGenerator.AltImageToVideoPreHandlers)
            {
                handler(genInfo);
            }

            WGNodeData effectiveSourceMedia = g.CurrentMedia ?? sourceMedia;
            PrepareModelAndConditioning(genInfo, effectiveSourceMedia);
            PrepareConditioning(
                genInfo,
                stageFrame,
                effectiveSourceMedia,
                guideMedia,
                skipGuideReinjection,
                applySourceVideoLatent,
                postVideoChain,
                clipRefs ?? [],
                guideMergeStrength);
            genInfo.VideoCFG ??= genInfo.DefaultCFG;

            foreach (
                Action<WorkflowGenerator.ImageToVideoGenInfo> handler in
                WorkflowGenerator.AltImageToVideoPostHandlers)
            {
                handler(genInfo);
            }

            ExecuteSampler(genInfo, stageFrame);
            FinalizeOutput(genInfo, stageFrame, effectiveSourceMedia, postVideoChain);
        }
        finally
        {
            g.IsImageToVideo = false;
        }
    }

    private void PrepareModelAndConditioning(WorkflowGenerator.ImageToVideoGenInfo genInfo, WGNodeData sourceMedia)
    {
        g.FinalLoadedModel = genInfo.VideoModel;
        (genInfo.VideoModel, genInfo.Model, WGNodeData clip, genInfo.Vae) = g.CreateModelLoader(
            genInfo.VideoModel,
            "image2video",
            null,
            true,
            sectionId: genInfo.ContextID);

        int width = sourceMedia.Width ?? g.UserInput.GetImageWidth();
        int height = sourceMedia.Height ?? g.UserInput.GetImageHeight();
        int steps = genInfo.Steps;
        double guidance = g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1);
        string positivePrompt = ExtractVideoConditioningPrompt(genInfo.Prompt);
        string negativePrompt = ExtractVideoConditioningPrompt(genInfo.NegativePrompt);

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput clipOutput = bridge.ResolvePath(clip.Path);

        SwarmClipTextEncodeAdvancedNode posCondNode = AddSwarmClipTextEncodeAdvanced(
            bridge, clipOutput, steps, positivePrompt, width, height, guidance);
        SwarmClipTextEncodeAdvancedNode negCondNode = AddSwarmClipTextEncodeAdvanced(
            bridge, clipOutput, steps, negativePrompt, width, height, guidance);
        BridgeSync.SyncLastId(g);

        genInfo.PosCond = new JArray(posCondNode.Id, 0);
        genInfo.NegCond = new JArray(negCondNode.Id, 0);
    }

    private static SwarmClipTextEncodeAdvancedNode AddSwarmClipTextEncodeAdvanced(
        WorkflowBridge bridge,
        INodeOutput clipOutput,
        int steps,
        string prompt,
        int width,
        int height,
        double guidance)
    {
        SwarmClipTextEncodeAdvancedNode node = bridge.AddNode(new SwarmClipTextEncodeAdvancedNode().With(
            Steps: steps,
            Prompt: prompt ?? "",
            Width: width,
            Height: height,
            TargetWidth: width,
            TargetHeight: height,
            Guidance: guidance));
        node.Clip.TryConnectToUntyped(clipOutput);
        bridge.SyncNode(node);
        return node;
    }

    private static string ExtractVideoConditioningPrompt(string prompt)
    {
        PromptRegion regionalizer = new(prompt ?? "");
        if (!string.IsNullOrWhiteSpace(regionalizer.VideoPrompt))
        {
            return regionalizer.VideoPrompt.Trim();
        }
        return regionalizer.GlobalPrompt.Trim();
    }

    private void PrepareConditioning(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        bool skipGuideReinjection,
        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent,
        LtxPostVideoChainCapture postVideoChain,
        IReadOnlyList<ResolvedClipRef> clipRefs,
        double guideMergeStrength)
    {
        WGNodeData stageLatent = BuildStageLatent(genInfo, stageFrame, sourceMedia, postVideoChain);
        if (stageLatent is null)
        {
            genInfo.PrepFullCond(g, guideMedia);
            applySourceVideoLatent?.Invoke(genInfo);
            return;
        }

        new LtxConditioningPipeline(g, genInfo, stageFrame, this)
            .WithLatent(stageLatent, sourceMedia)
            .WithUpscaleIfNeeded(sourceMedia)
            .WithInplaceMerges(clipRefs)
            .BindToCurrentMedia(skipGuideReinjection, guideMedia, guideMergeStrength)
            .WithLtxvConditioning()
            .WithGuideAdditions(clipRefs);
    }

    internal string CreateLtxvImgToVideoInplaceNode(
        JToken vaePath,
        JArray preprocessedImagePath,
        JArray latentPath,
        double strength,
        bool bypass)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LTXVImgToVideoInplaceNode node = bridge.AddNode(new LTXVImgToVideoInplaceNode().With(
            Strength: strength,
            Bypass: bypass));
        if (vaePath is JArray vaeArr)
        {
            node.Vae.ConnectFromPath(bridge, vaeArr);
        }
        node.Image.ConnectFromPath(bridge, preprocessedImagePath);
        node.LatentInput.ConnectFromPath(bridge, latentPath);
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);
        return node.Id;
    }

    private WGNodeData BuildStageLatent(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        LtxPostVideoChainCapture postVideoChain)
    {
        StageSpec stage = stageFrame.Stage;
        ClipSpec clip = stageFrame.ClipContext.Clip;
        genInfo.StartStep = (int)Math.Floor(stage.Steps * (1 - stage.Control));
        JArray controlNetLengthFrames = TryResolveControlNetLengthFrames(clip);

        if (rootVideoStageHandoff.ShouldReplaceTextToVideoRootStage(stage))
        {
            return CreateEmptyVideoLatent(genInfo, clip, sourceMedia, controlNetLengthFrames);
        }

        if (postVideoChain?.CanReuseCurrentOutputAsStageInput(sourceMedia) == true)
        {
            WGNodeData nativeStageInput = postVideoChain.CreateStageInput();
            WGNodeData nativeVideoLatent = nativeStageInput.AsLatentImage(genInfo.Vae);
            postVideoChain.AttachSourceAudio(nativeVideoLatent);
            return EnsureHasAudioWithLtxFps(nativeVideoLatent, genInfo, sourceMedia);
        }

        if (!genInfo.Frames.HasValue && controlNetLengthFrames is null)
        {
            return null;
        }
        if (sourceMedia?.DataType == WGNodeData.DT_IMAGE)
        {
            int width = Math.Max(sourceMedia.Width ?? g.UserInput.GetImageWidth(), 16);
            int height = Math.Max(sourceMedia.Height ?? g.UserInput.GetImageHeight(), 16);
            return CreateEmptyVideoLatentWithOptionalAudioLength(
                clip,
                genInfo,
                sourceMedia,
                width,
                height,
                genInfo.Frames ?? sourceMedia.Frames ?? DefaultFrameCount,
                sourceMedia.AttachedAudio,
                controlNetLengthFrames);
        }

        if (TryGetReusableDecodedVideoLatent(
                sourceMedia,
                genInfo,
                allowDynamicFrameCount: controlNetLengthFrames is not null,
                out JArray reusableLatentPath))
        {
            WGNodeData reusedLatent = sourceMedia.WithPath(
                reusableLatentPath,
                WGNodeData.DT_LATENT_VIDEO,
                genInfo.Vae.Compat);
            reusedLatent.Frames = genInfo.Frames.HasValue
                ? Math.Min(genInfo.Frames.Value, reusedLatent.Frames ?? int.MaxValue)
                : null;
            return EnsureHasAudioWithLtxFps(reusedLatent, genInfo, sourceMedia);
        }

        WGNodeData sourceSnapshot = sourceMedia;
        if (postVideoChain is not null && ReferencesCurrentOutputPath(sourceMedia, postVideoChain))
        {
            sourceSnapshot = postVideoChain.CreateDetachedGuideMedia(genInfo.Vae);
        }

        string fromBatch = AddImageFromBatch(
            sourceSnapshot.Path,
            batchIndex: 0,
            length: FrameCountToken(controlNetLengthFrames, genInfo.Frames ?? DefaultFrameCount));
        WGNodeData stageVideoInput = sourceSnapshot.WithPath([fromBatch, 0]);
        stageVideoInput.Frames = controlNetLengthFrames is null
            ? Math.Min(genInfo.Frames ?? DefaultFrameCount, stageVideoInput.Frames ?? int.MaxValue)
            : null;
        WGNodeData encodedLatent = stageVideoInput.AsLatentImage(genInfo.Vae);
        return EnsureHasAudioWithLtxFps(encodedLatent, genInfo, sourceMedia);
    }

    private string AddImageFromBatch(JArray imagePath, int batchIndex, JToken length)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode().With(
            BatchIndex: batchIndex));
        node.Image.TryConnectFromPath(bridge, imagePath);
        SetIntInputFromToken(node.Length, length, bridge);
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);
        return node.Id;
    }

    private static void SetIntInputFromToken(NodeInput<IntType> input, JToken token, WorkflowBridge bridge)
    {
        if (token is JArray arr && input.TryConnectFromPath(bridge, arr))
        {
            return;
        }
        if (token is JValue v && v.Value is not null)
        {
            input.Set(Convert.ToInt64(v.Value));
        }
    }

    private WGNodeData CreateEmptyVideoLatent(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ClipSpec clip,
        WGNodeData sourceMedia,
        JArray controlNetLengthFrames = null)
    {
        int width = Math.Max(sourceMedia?.Width ?? g.UserInput.GetImageWidth(), 16);
        int height = Math.Max(sourceMedia?.Height ?? g.UserInput.GetImageHeight(), 16);
        int frames = genInfo.Frames ?? sourceMedia?.Frames ?? DefaultFrameCount;
        WGNodeData attachedAudio = sourceMedia?.AttachedAudio;
        return CreateEmptyVideoLatentWithOptionalAudioLength(
            clip,
            genInfo,
            sourceMedia,
            width,
            height,
            frames,
            attachedAudio,
            controlNetLengthFrames);
    }

    private WGNodeData CreateEmptyVideoLatentWithOptionalAudioLength(
        ClipSpec clip,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia,
        int width,
        int height,
        int frames,
        WGNodeData attachedAudio,
        JArray controlNetLengthFrames = null)
    {
        int fps = ResolveFps(genInfo, sourceMedia);
        JArray audioLengthFrames = null;
        WGNodeData effectiveAttached = attachedAudio;

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);

        if (controlNetLengthFrames is null
            && ShouldMatchStageLengthToAudio(clip)
            && effectiveAttached?.Path is JToken audioPath)
        {
            JToken lengthFramesAudioSource = LtxAudioPathResolution.ResolveLengthToFramesAudioSource(
                bridge,
                audioPath,
                null);

            SwarmAudioLengthToFramesNode lengthToFrames = bridge.AddNode(new SwarmAudioLengthToFramesNode()).With(
                FrameRate: fps);
            if (lengthFramesAudioSource is JArray audioSourceArr)
            {
                lengthToFrames.AudioInput.TryConnectFromPath(bridge, audioSourceArr);
            }
            bridge.SyncNode(lengthToFrames);

            audioLengthFrames = new JArray(lengthToFrames.Id, 1);
            effectiveAttached = new WGNodeData(
                new JArray(lengthToFrames.Id, 0),
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae?.Compat ?? effectiveAttached.Compat);
        }

        JArray dynamicLengthFrames = controlNetLengthFrames ?? audioLengthFrames;
        JToken latentLength = dynamicLengthFrames is null
            ? new JValue(frames)
            : LtxFrameCountConnector.CloneConnection(dynamicLengthFrames);

        EmptyLTXVLatentVideoNode emptyNode = bridge.AddNode(new EmptyLTXVLatentVideoNode());
        emptyNode.With(
            Width: width,
            Height: height,
            BatchSize: 1);
        SetIntInputFromToken(emptyNode.Length, latentLength, bridge);
        bridge.SyncNode(emptyNode);
        BridgeSync.SyncLastId(g);

        WGNodeData stageLatent = new([emptyNode.Id, 0], g, WGNodeData.DT_LATENT_VIDEO, genInfo.Model.Compat)
        {
            Width = width,
            Height = height,
            Frames = dynamicLengthFrames is null ? frames : null,
            FPS = fps,
            AttachedAudio = effectiveAttached
        };
        WGNodeData withAudio = stageLatent.EnsureHasAudioIfNeeded(genInfo.Vae, g.CurrentAudioVae);
        PatchLtxEmptyLatentAudioAfterEnsure(stageLatent, withAudio, fps, dynamicLengthFrames);

        return withAudio;
    }

    private WGNodeData EnsureHasAudioWithLtxFps(
        WGNodeData latent,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia)
    {
        int fps = ResolveFps(genInfo, sourceMedia);
        WGNodeData withAudio = latent.EnsureHasAudioIfNeeded(genInfo.Vae, g.CurrentAudioVae);
        PatchLtxEmptyLatentAudioAfterEnsure(latent, withAudio, fps);
        withAudio.FPS = fps;

        return withAudio;
    }

    private void PatchLtxEmptyLatentAudioAfterEnsure(
        WGNodeData latentBefore,
        WGNodeData latentAfter,
        int frameRate,
        JArray framesConnection = null)
    {
        if (ReferenceEquals(latentBefore, latentAfter) || frameRate <= 0)
        {
            return;
        }
        if (latentAfter.AttachedAudio?.Path is not JArray audioPath || audioPath.Count < 1)
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        string emptyAudioNodeId = $"{audioPath[0]}";
        if (bridge.Graph.GetNode<LTXVEmptyLatentAudioNode>(emptyAudioNodeId) is not LTXVEmptyLatentAudioNode emptyAudio)
        {
            return;
        }
        if (framesConnection is JArray framesArr)
        {
            emptyAudio.FramesNumber.TryConnectFromPath(bridge, framesArr);
        }
        emptyAudio.FrameRate.Set(frameRate);
        bridge.SyncNode(emptyAudio);
    }

    private JArray TryResolveControlNetLengthFrames(ClipSpec clip)
    {
        if (!clip.ClipLengthFromControlNet)
        {
            return null;
        }
        return new ControlNetApplicator(g).TryCreateCapturedControlImageFrameCount(
            clip.ControlNetSource,
            out JArray framesConnection)
            ? framesConnection
            : null;
    }

    private static JToken FrameCountToken(JArray framesConnection, int fallbackFrames) =>
        framesConnection is null
            ? new JValue(fallbackFrames)
            : LtxFrameCountConnector.CloneConnection(framesConnection);

    private bool TryGetReusableDecodedVideoLatent(
        WGNodeData sourceMedia,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        bool allowDynamicFrameCount,
        out JArray latentPath)
    {
        latentPath = null;
        if (sourceMedia?.DataType != WGNodeData.DT_VIDEO
            || sourceMedia.Path is not JArray { Count: 2 } sourcePath
            || genInfo?.Vae?.Path is not JArray { Count: 2 } vaePath
            || (!genInfo.Frames.HasValue && !allowDynamicFrameCount)
            || genInfo.Frames.HasValue
                && sourceMedia.Frames is int sourceFrames
                && sourceFrames > genInfo.Frames.Value)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ComfyNode sourceNode = bridge.Graph.GetNode($"{sourcePath[0]}");
        if (sourceNode is not (VAEDecodeNode or VAEDecodeTiledNode))
        {
            return false;
        }
        INodeOutput samplesConn = sourceNode.FindInput("samples")?.Connection;
        INodeOutput decodeVaeConn = sourceNode.FindInput("vae")?.Connection;
        if (samplesConn is null || decodeVaeConn is null)
        {
            return false;
        }

        bool sameVaeNode = decodeVaeConn.Node.Id == $"{vaePath[0]}"
            && decodeVaeConn.SlotIndex == (int)vaePath[1];
        bool sameDynamicLtxCompat =
            allowDynamicFrameCount
            && !string.IsNullOrWhiteSpace(sourceMedia.Compat?.ID)
            && sourceMedia.Compat.ID == genInfo.Vae.Compat?.ID;
        if (!sameVaeNode && !sameDynamicLtxCompat)
        {
            return false;
        }

        latentPath = new JArray(samplesConn.Node.Id, samplesConn.SlotIndex);
        return true;
    }

    private static bool ShouldMatchStageLengthToAudio(ClipSpec clip)
    {
        if (!clip.ClipLengthFromAudio)
        {
            return false;
        }
        return ClipAudioWorkflowHelper.IsExternalClipAudioSource(clip.AudioSource);
    }

    internal void ApplyResolvedFpsToWorkflow(WorkflowGenerator.ImageToVideoGenInfo genInfo, int fps)
    {
        if (fps <= 0)
        {
            return;
        }
        genInfo.VideoFPS = fps;
        g.UserInput.Set(T2IParamTypes.VideoFPS, fps, genInfo.ContextID);
    }

    internal int ResolveFps(WorkflowGenerator.ImageToVideoGenInfo genInfo, WGNodeData sourceMedia)
    {
        int? fps = genInfo.VideoFPS ?? sourceMedia.FPS;
        if (fps.HasValue && fps.Value > 0)
        {
            return fps.Value;
        }
        int specFps = g.GetVideoStagesSpec().FPS;
        return specFps > 0 ? specFps : DefaultFps;
    }

    private static bool ReferencesCurrentOutputPath(WGNodeData media, LtxPostVideoChainCapture postVideoChain)
    {
        if (media?.Path is not JArray mediaPath || postVideoChain is null)
        {
            return false;
        }

        return JToken.DeepEquals(mediaPath, postVideoChain.CurrentOutputMedia?.Path)
            || JToken.DeepEquals(mediaPath, postVideoChain.DecodeOutputPath);
    }

    internal JArray ResolvePreprocessedGuidePath(JArray guideImagePath, WGNodeData targetMedia)
    {
        JArray scaledGuidePath = EnsureClipResolutionBeforeLtxvPreprocess(guideImagePath, targetMedia);
        if (TryFindReusablePreprocessOutput(scaledGuidePath, out JArray reusedPath))
        {
            return reusedPath;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LTXVPreprocessNode preprocess = bridge.AddNode(new LTXVPreprocessNode().With(
            ImgCompression: ImgCompression));
        preprocess.Image.TryConnectFromPath(bridge, scaledGuidePath);
        bridge.SyncNode(preprocess);
        BridgeSync.SyncLastId(g);

        return new JArray(preprocess.Id, 0);
    }

    private JArray EnsureClipResolutionBeforeLtxvPreprocess(JArray guideImagePath, WGNodeData targetMedia)
    {
        if (guideImagePath is not { Count: 2 })
        {
            return guideImagePath;
        }

        int targetW = Math.Max(16, targetMedia?.Width ?? 0);
        int targetH = Math.Max(16, targetMedia?.Height ?? 0);
        if (targetMedia?.Width is null
            || targetMedia.Height is null)
        {
            if (!rootVideoStageResizer.TryGetRootStageResolution(out targetW, out targetH))
            {
                targetW = Math.Max(16, g.UserInput.GetImageWidth());
                targetH = Math.Max(16, g.UserInput.GetImageHeight());
            }
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (TryGetExistingScaleAtTargetDimensions(
                bridge,
                guideImagePath,
                targetW,
                targetH,
                out ImageScaleNode existing))
        {
            existing.Crop.Set("center");
            bridge.SyncNode(existing);
            return guideImagePath;
        }

        JArray scaleSourcePath = ResolveImageScaleBaseSource(bridge, guideImagePath);
        if (TryFindReusableImageScale(bridge, scaleSourcePath, targetW, targetH, out ImageScaleNode reusable))
        {
            reusable.Crop.Set("center");
            bridge.SyncNode(reusable);
            return new JArray(reusable.Id, 0);
        }

        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode().With(
            Width: targetW,
            Height: targetH,
            UpscaleMethod: "lanczos",
            Crop: "center"));
        scale.Image.TryConnectFromPath(bridge, scaleSourcePath);
        bridge.SyncNode(scale);
        BridgeSync.SyncLastId(g);

        return new JArray(scale.Id, 0);
    }

    private static bool TryGetExistingScaleAtTargetDimensions(
        WorkflowBridge bridge,
        JArray imagePath,
        int targetW,
        int targetH,
        out ImageScaleNode scale)
    {
        scale = imagePath is { Count: 2 } ? bridge.Graph.GetNode<ImageScaleNode>($"{imagePath[0]}") : null;
        return scale is not null
            && scale.Width.LiteralAsInt() == targetW
            && scale.Height.LiteralAsInt() == targetH;
    }

    private static JArray ResolveImageScaleBaseSource(WorkflowBridge bridge, JArray imagePath)
    {
        if (imagePath is not { Count: 2 })
        {
            return imagePath;
        }

        ComfyNode current = bridge.Graph.GetNode($"{imagePath[0]}");
        int currentSlot = (int)imagePath[1];
        HashSet<string> visited = [];
        while (current is ImageScaleNode scale && visited.Add($"{scale.Id}::{currentSlot}"))
        {
            INodeOutput upstream = scale.Image.Connection;
            if (upstream is null)
            {
                break;
            }
            current = upstream.Node;
            currentSlot = upstream.SlotIndex;
        }
        return current is null ? imagePath : new JArray(current.Id, currentSlot);
    }

    private static bool TryFindReusableImageScale(
        WorkflowBridge bridge,
        JArray sourcePath,
        int targetW,
        int targetH,
        out ImageScaleNode scale)
    {
        scale = null;
        if (sourcePath is not { Count: 2 })
        {
            return false;
        }
        string sourceId = $"{sourcePath[0]}";
        int sourceSlot = (int)sourcePath[1];

        foreach (ImageScaleNode candidate in bridge.Graph.NodesOfType<ImageScaleNode>())
        {
            INodeOutput candidateImage = candidate.Image.Connection;
            if (candidateImage?.Node.Id != sourceId
                || candidateImage.SlotIndex != sourceSlot
                || candidate.Width.LiteralAsInt() != targetW
                || candidate.Height.LiteralAsInt() != targetH)
            {
                continue;
            }
            scale = candidate;
            return true;
        }
        return false;
    }

    private bool TryFindReusablePreprocessOutput(JArray guideImagePath, out JArray preprocessOutputPath)
    {
        preprocessOutputPath = null;
        if (guideImagePath is not { Count: 2 })
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (TryResolveReusablePreprocessNode(bridge, guideImagePath, out string preprocessNodeId))
        {
            preprocessOutputPath = new JArray(preprocessNodeId, 0);
            return true;
        }

        INodeOutput startOutput = bridge.ResolvePath(guideImagePath);
        if (startOutput is null)
        {
            return false;
        }

        Queue<INodeOutput> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(startOutput);
        while (pending.Count > 0)
        {
            INodeOutput currentOutput = pending.Dequeue();
            string outputKey = $"{currentOutput.Node.Id}::{currentOutput.SlotIndex}";
            if (!visited.Add(outputKey))
            {
                continue;
            }

            foreach ((ComfyNode consumer, INodeInput input) in bridge.Graph.FindInputsConnectedTo(currentOutput))
            {
                if (input.Name != "image")
                {
                    continue;
                }

                if (consumer is LTXVPreprocessNode preprocess && HasMatchingImgCompression(preprocess))
                {
                    preprocessOutputPath = new JArray(preprocess.Id, 0);
                    return true;
                }

                if (consumer is ImageScaleNode && consumer.Outputs.Count > 0)
                {
                    pending.Enqueue(consumer.Outputs[0]);
                }
            }
        }

        return false;
    }

    private static bool TryResolveReusablePreprocessNode(
        WorkflowBridge bridge,
        JArray imagePath,
        out string preprocessNodeId)
    {
        preprocessNodeId = $"{imagePath[0]}";
        if ((int)imagePath[1] != 0)
        {
            return false;
        }
        return bridge.Graph.GetNode<LTXVPreprocessNode>(preprocessNodeId) is LTXVPreprocessNode preprocess
            && HasMatchingImgCompression(preprocess);
    }

    private static bool HasMatchingImgCompression(LTXVPreprocessNode preprocess) =>
        preprocess.ImgCompression.LiteralAsInt() == ImgCompression;

    private void ExecuteSampler(WorkflowGenerator.ImageToVideoGenInfo genInfo, StageFrame stageFrame)
    {
        string previewType = g.UserInput.Get(ComfyUIBackendExtension.VideoPreviewType, "animate");
        string explicitSampler = g.UserInput.Get(
            ComfyUIBackendExtension.SamplerParam,
            null,
            sectionId: genInfo.ContextID,
            includeBase: false);
        string explicitScheduler = g.UserInput.Get(
            ComfyUIBackendExtension.SchedulerParam,
            null,
            sectionId: genInfo.ContextID,
            includeBase: false);

        g.CurrentMedia = g.CurrentMedia.AsSamplingLatent(genInfo.Vae, g.CurrentAudioVae);
        LtxAudioMaskResizer.ApplyCurrentAudioMaskDimensions(g.CurrentMedia);
        string samplerNode = g.CreateKSampler(
            genInfo.Model.Path,
            genInfo.PosCond,
            genInfo.NegCond,
            g.CurrentMedia.Path,
            genInfo.VideoCFG.Value,
            genInfo.Steps,
            genInfo.StartStep,
            10000,
            genInfo.Seed,
            returnWithLeftoverNoise: false,
            addNoise: true,
            sigmin: 0.002,
            sigmax: 1000,
            previews: previewType,
            defsampler: genInfo.DefaultSampler,
            defscheduler: genInfo.DefaultScheduler,
            hadSpecialCond: genInfo.HadSpecialCond,
            explicitSampler: explicitSampler,
            explicitScheduler: explicitScheduler,
            sectionId: genInfo.ContextID
        );

        g.CurrentMedia = g.CurrentMedia.WithPath([samplerNode, 0]);
        g.CurrentMedia.Frames = genInfo.Frames ?? g.CurrentMedia.Frames;
        g.CurrentMedia.FPS = genInfo.VideoFPS ?? g.CurrentMedia.FPS;

        if (stageFrame.NeedsCropGuidesAfterSampler)
        {
            CropGuidesAfterSampler(genInfo);
        }

        if (genInfo.DoFirstFrameLatentSwap is not null)
        {
            ApplyFirstFrameLatentSwap(genInfo);
        }
    }

    private void ApplyFirstFrameLatentSwap(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ReplaceVideoLatentFramesNode replace = bridge.AddNode(new ReplaceVideoLatentFramesNode().With(
            Index: 0));
        if (g.CurrentMedia?.Path is JArray destPath)
        {
            replace.Destination.ConnectFromPath(bridge, destPath);
        }
        if (genInfo.DoFirstFrameLatentSwap is JArray sourcePath)
        {
            replace.Source.TryConnectFromPath(bridge, sourcePath);
        }
        bridge.SyncNode(replace);

        NormalizeVideoLatentStartNode normalize = bridge.AddNode(new NormalizeVideoLatentStartNode().With(
            StartFrameCount: 4,
            ReferenceFrameCount: 5));
        normalize.LatentInput.ConnectTo(replace.LATENT);
        bridge.SyncNode(normalize);
        BridgeSync.SyncLastId(g);

        g.CurrentMedia = g.CurrentMedia.WithPath([normalize.Id, 0]);
    }

    private void CropGuidesAfterSampler(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        bool shouldRestoreAudioVideoLatent = g.CurrentMedia.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO;

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput cropLatentSource;
        INodeOutput audioLatentSource = null;
        if (shouldRestoreAudioVideoLatent)
        {
            LTXVSeparateAVLatentNode separate = bridge.AddNode(new LTXVSeparateAVLatentNode());
            if (g.CurrentMedia?.Path is JArray avPath)
            {
                separate.AvLatent.ConnectFromPath(bridge, avPath);
            }
            bridge.SyncNode(separate);
            cropLatentSource = separate.VideoLatent;
            audioLatentSource = separate.AudioLatent;
        }
        else
        {
            cropLatentSource = g.CurrentMedia?.Path is JArray latentPath ? bridge.ResolvePath(latentPath) : null;
        }

        LTXVCropGuidesNode crop = bridge.AddNode(new LTXVCropGuidesNode());
        crop.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond);
        crop.NegativeInput.ConnectFromPath(bridge, genInfo.NegCond);
        crop.LatentInput.ConnectToUntyped(cropLatentSource);
        bridge.SyncNode(crop);

        genInfo.PosCond = [crop.Id, 0];
        genInfo.NegCond = [crop.Id, 1];

        if (shouldRestoreAudioVideoLatent)
        {
            LTXVConcatAVLatentNode concat = bridge.AddNode(new LTXVConcatAVLatentNode());
            concat.VideoLatent.ConnectTo(crop.Latent);
            concat.AudioLatent.TryConnectToUntyped(audioLatentSource);
            bridge.SyncNode(concat);
            BridgeSync.SyncLastId(g);

            g.CurrentMedia = g.CurrentMedia.WithPath(
                [concat.Id, 0],
                WGNodeData.DT_LATENT_AUDIOVIDEO,
                genInfo.Model.Compat);
            return;
        }

        BridgeSync.SyncLastId(g);
        g.CurrentMedia = g.CurrentMedia.WithPath([crop.Id, 2], null, genInfo.Model.Compat);
    }

    private void FinalizeOutput(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        LtxPostVideoChainCapture postVideoChain)
    {
        int outputWidth = g.CurrentMedia?.Width ?? sourceMedia.Width ?? g.UserInput.GetImageWidth();
        int outputHeight = g.CurrentMedia?.Height ?? sourceMedia.Height ?? g.UserInput.GetImageHeight();
        bool splicedIntoNativeChain = postVideoChain is not null;
        bool parallelMultiClip = stageFrame.ParallelMultiClip;
        if (splicedIntoNativeChain)
        {
            if (parallelMultiClip)
            {
                LtxPostVideoChainSplicer.SpliceCurrentOutputToDedicatedBranch(
                    postVideoChain,
                    g,
                    genInfo.Vae,
                    outputWidth,
                    outputHeight,
                    genInfo.Frames,
                    genInfo.VideoFPS);
            }
            else
            {
                LtxPostVideoChainSplicer.SpliceCurrentOutput(postVideoChain, g, genInfo.Vae);
            }

            if (postVideoChain.HasPostDecodeWrappers)
            {
                ApplyCurrentMediaOutputMetadata(
                    outputWidth,
                    outputHeight,
                    postVideoChain.CurrentOutputMedia.Frames,
                    postVideoChain.CurrentOutputMedia.FPS);
            }
            else
            {
                ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, genInfo.Frames, genInfo.VideoFPS);
            }
            AttachDecodedLtxAudioFromCurrentVideo();
        }
        else
        {
            g.CurrentMedia = VaeDecodePreference.AsRawImage(g, g.CurrentMedia, genInfo.Vae);
            AttachDecodedLtxAudioFromCurrentVideo();
            ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, genInfo.Frames, genInfo.VideoFPS);
        }

        int trimStartFrames = g.UserInput.Get(T2IParamTypes.TrimVideoStartFrames, 0);
        int trimEndFrames = g.UserInput.Get(T2IParamTypes.TrimVideoEndFrames, 0);
        bool hasRequestedTrim = trimStartFrames != 0 || trimEndFrames != 0;
        bool shouldApplyTrim = hasRequestedTrim
            && !(splicedIntoNativeChain && postVideoChain.HasPostDecodeWrappers);
        if (shouldApplyTrim)
        {
            string trimNodeId = AddSwarmTrimFrames(g.CurrentMedia.Path, trimStartFrames, trimEndFrames);
            g.CurrentMedia = g.CurrentMedia.WithPath([trimNodeId, 0]);
            if (splicedIntoNativeChain && !postVideoChain.HasPostDecodeWrappers && !parallelMultiClip)
            {
                LtxPostVideoChainSplicer.RetargetAnimationSaves(postVideoChain, g, g.CurrentMedia.Path);
            }
            ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, genInfo.Frames, genInfo.VideoFPS);
        }

        g.CurrentVae = genInfo.Vae;
    }

    private string AddSwarmTrimFrames(JArray imagePath, int trimStart, int trimEnd)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        SwarmTrimFramesNode node = bridge.AddNode(new SwarmTrimFramesNode().With(
            TrimStart: trimStart,
            TrimEnd: trimEnd));
        node.Image.TryConnectFromPath(bridge, imagePath);
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);
        return node.Id;
    }

    private void AttachDecodedLtxAudioFromCurrentVideo()
    {
        if (g.CurrentMedia?.Path is not JArray { Count: 2 } currentPath)
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        MediaRef currentMedia = MediaRef.FromWGNodeData(g.CurrentMedia, bridge);
        if (currentMedia is null)
        {
            return;
        }

        MediaRef audioVae = ResolveAudioVaeMediaRef(bridge);
        if (audioVae is null)
        {
            return;
        }

        LtxChainOps.AttachDecodedLtxAudio(bridge, currentMedia, audioVae);
        BridgeSync.SyncLastId(g);

        if (currentMedia.AttachedAudio is not null)
        {
            g.CurrentMedia.AttachedAudio = currentMedia.AttachedAudio.ToWGNodeData(g);
        }
    }

    private MediaRef ResolveAudioVaeMediaRef(WorkflowBridge bridge)
    {
        MediaRef audioVae = MediaRef.FromWGNodeData(g.CurrentAudioVae, bridge);
        if (audioVae is not null)
        {
            return audioVae;
        }

        LTXVAudioVAEDecodeNode existingAudioDecode = bridge.Graph
            .NodesOfType<LTXVAudioVAEDecodeNode>()
            .FirstOrDefault();
        if (existingAudioDecode?.AudioVae.Connection is not INodeOutput audioVaeOutput)
        {
            return null;
        }

        return new MediaRef
        {
            Output = audioVaeOutput,
            DataType = WGNodeData.DT_AUDIO,
            Compat = g.CurrentAudioVae?.Compat
        };
    }

    private void ApplyCurrentMediaOutputMetadata(int width, int height, int? frames, int? fps)
    {
        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
        g.CurrentMedia.Frames = frames ?? g.CurrentMedia.Frames;
        g.CurrentMedia.FPS = fps ?? g.CurrentMedia.FPS;
    }
}
