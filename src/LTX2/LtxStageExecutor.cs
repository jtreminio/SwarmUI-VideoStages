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

internal sealed record ResolvedClipRef(WGNodeData Image, JsonParser.RefSpec Spec, double Strength);

internal sealed class LtxStageExecutor(
    WorkflowGenerator g,
    RootVideoStageTakeover rootVideoStageTakeover,
    RootVideoStageResizer rootVideoStageResizer,
    JsonParser jsonParser,
    LtxAudioMaskResizer audioMaskResizer)
{
    private bool _needsLtxvCropGuidesAfterSampler;

    private const int ImgCompression = 18;
    private const double DefaultGuideMergeStrength = 1.0;
    private const int DefaultFps = 24;
    private const int DefaultFrameCount = 97;
    private const double DefaultCfg = 3;
    private const string DefaultSampler = "euler";
    private const string DefaultScheduler = "normal";

    public void RunStage(
        JsonParser.StageSpec stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        bool skipGuideReinjection,
        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent,
        LtxPostVideoChain postVideoChain,
        IReadOnlyList<ResolvedClipRef> clipRefs = null,
        double guideMergeStrength = DefaultGuideMergeStrength)
    {
        postVideoChain?.AttachSourceAudio(sourceMedia);

        _needsLtxvCropGuidesAfterSampler = false;
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
                stage,
                effectiveSourceMedia,
                guideMedia,
                skipGuideReinjection,
                applySourceVideoLatent,
                postVideoChain,
                clipRefs ?? Array.Empty<ResolvedClipRef>(),
                guideMergeStrength);
            genInfo.VideoCFG ??= genInfo.DefaultCFG;

            foreach (
                Action<WorkflowGenerator.ImageToVideoGenInfo> handler in
                WorkflowGenerator.AltImageToVideoPostHandlers)
            {
                handler(genInfo);
            }
            if (ControlNetApplicator.ConsumeNeedsLtxIcloraGuideCrop(g))
            {
                _needsLtxvCropGuidesAfterSampler = true;
            }

            ExecuteSampler(genInfo);
            FinalizeOutput(genInfo, effectiveSourceMedia, postVideoChain);
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
        INodeOutput clipOutput = bridge.ResolvePath(clip.Path as JArray);

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
        SwarmClipTextEncodeAdvancedNode node = bridge.AddNode(new SwarmClipTextEncodeAdvancedNode());
        if (clipOutput is not null)
        {
            node.Clip.ConnectToUntyped(clipOutput);
        }
        node.Steps.Set(steps);
        node.Prompt.Set(prompt ?? "");
        node.Width.Set(width);
        node.Height.Set(height);
        node.TargetWidth.Set(width);
        node.TargetHeight.Set(height);
        node.Guidance.Set(guidance);
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
        JsonParser.StageSpec stage,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        bool skipGuideReinjection,
        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent,
        LtxPostVideoChain postVideoChain,
        IReadOnlyList<ResolvedClipRef> clipRefs,
        double guideMergeStrength)
    {
        WGNodeData stageLatent = BuildStageLatent(genInfo, stage, sourceMedia, postVideoChain);
        if (stageLatent is null)
        {
            genInfo.PrepFullCond(g, guideMedia);
            applySourceVideoLatent?.Invoke(genInfo);
            return;
        }

        ApplyResolvedFpsToWorkflow(genInfo, ResolveFps(genInfo, sourceMedia));
        genInfo.VideoFPS ??= DefaultFps;
        genInfo.Frames ??= DefaultFrameCount;
        genInfo.DefaultCFG = DefaultCfg;
        genInfo.HadSpecialCond = true;
        genInfo.DefaultSampler = DefaultSampler;
        genInfo.DefaultScheduler = DefaultScheduler;
        stageLatent = ApplyStageUpscaleIfNeeded(stage, genInfo, stageLatent, sourceMedia);
        stageLatent = ApplyClipReferenceInplaceMerges(genInfo, stageLatent, clipRefs);

        if (skipGuideReinjection)
        {
            g.CurrentMedia = stageLatent;
        }
        else
        {
            JArray preprocessedGuidePath = ResolvePreprocessedGuidePath(guideMedia.Path, stageLatent);
            string imgToVideoNode = CreateLtxvImgToVideoInplaceNode(
                genInfo.Vae.Path,
                preprocessedGuidePath,
                stageLatent.Path,
                guideMergeStrength,
                bypass: false);
            g.CurrentMedia = stageLatent.WithPath(
                [imgToVideoNode, 0],
                WGNodeData.DT_LATENT_VIDEO,
                genInfo.Model.Compat);
        }

        AppendLtxvConditioning(genInfo);
        ApplyClipReferenceAddGuides(genInfo, clipRefs);
    }

    private void AppendLtxvConditioning(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LTXVConditioningNode cond = bridge.AddNode(new LTXVConditioningNode());
        if (bridge.ResolvePath(genInfo.PosCond) is INodeOutput pos) { cond.PositiveInput.ConnectToUntyped(pos); }
        if (bridge.ResolvePath(genInfo.NegCond) is INodeOutput neg) { cond.NegativeInput.ConnectToUntyped(neg); }
        if (genInfo.VideoFPS.HasValue) { cond.FrameRate.Set((double)genInfo.VideoFPS.Value); }
        bridge.SyncNode(cond);
        BridgeSync.SyncLastId(g);

        genInfo.PosCond = [cond.Id, 0];
        genInfo.NegCond = [cond.Id, 1];
    }

    private string CreateLtxvImgToVideoInplaceNode(
        JToken vaePath,
        JArray preprocessedImagePath,
        JArray latentPath,
        double strength,
        bool bypass)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LTXVImgToVideoInplaceNode node = bridge.AddNode(new LTXVImgToVideoInplaceNode());
        if (vaePath is JArray vaeArr && bridge.ResolvePath(vaeArr) is INodeOutput vae) { node.Vae.ConnectToUntyped(vae); }
        if (bridge.ResolvePath(preprocessedImagePath) is INodeOutput img) { node.Image.ConnectToUntyped(img); }
        if (bridge.ResolvePath(latentPath) is INodeOutput latent) { node.LatentInput.ConnectToUntyped(latent); }
        node.Strength.Set(strength);
        node.Bypass.Set(bypass);
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);
        return node.Id;
    }

    private static bool UseLtxvInplaceForRef(JsonParser.RefSpec spec)
    {
        return !spec.FromEnd && spec.Frame == 1;
    }

    private static int ComputeLtxvAddGuideFrameIndex(JsonParser.RefSpec spec)
    {
        if (spec.FromEnd)
        {
            return -Math.Max(1, spec.Frame);
        }

        return Math.Max(1, spec.Frame);
    }

    private WGNodeData ApplyClipReferenceInplaceMerges(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData stageLatent,
        IReadOnlyList<ResolvedClipRef> clipRefs)
    {
        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (!UseLtxvInplaceForRef(clipRef.Spec))
            {
                continue;
            }

            JArray preprocessed = ResolvePreprocessedGuidePath(clipRef.Image.Path, stageLatent);
            string imgToVideoNode = CreateLtxvImgToVideoInplaceNode(
                genInfo.Vae.Path,
                preprocessed,
                stageLatent.Path,
                clipRef.Strength,
                bypass: false);
            stageLatent = stageLatent.WithPath([imgToVideoNode, 0], WGNodeData.DT_LATENT_VIDEO, genInfo.Model.Compat);
        }

        return stageLatent;
    }

    private void ApplyClipReferenceAddGuides(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        IReadOnlyList<ResolvedClipRef> clipRefs)
    {
        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (UseLtxvInplaceForRef(clipRef.Spec))
            {
                continue;
            }

            JArray preprocessed = ResolvePreprocessedGuidePath(clipRef.Image.Path, g.CurrentMedia);
            int frameIdx = ComputeLtxvAddGuideFrameIndex(clipRef.Spec);

            WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            LTXVAddGuideNode addGuide = bridge.AddNode(new LTXVAddGuideNode());
            if (bridge.ResolvePath(genInfo.PosCond) is INodeOutput pos) { addGuide.PositiveInput.ConnectToUntyped(pos); }
            if (bridge.ResolvePath(genInfo.NegCond) is INodeOutput neg) { addGuide.NegativeInput.ConnectToUntyped(neg); }
            if (genInfo.Vae?.Path is JArray vaePath && bridge.ResolvePath(vaePath) is INodeOutput vae) { addGuide.Vae.ConnectToUntyped(vae); }
            if (g.CurrentMedia?.Path is JArray latentPath && bridge.ResolvePath(latentPath) is INodeOutput latent) { addGuide.LatentInput.ConnectToUntyped(latent); }
            if (bridge.ResolvePath(preprocessed) is INodeOutput img) { addGuide.Image.ConnectToUntyped(img); }
            addGuide.FrameIdx.Set(frameIdx);
            addGuide.Strength.Set(clipRef.Strength);
            bridge.SyncNode(addGuide);
            BridgeSync.SyncLastId(g);

            _needsLtxvCropGuidesAfterSampler = true;
            genInfo.PosCond = [addGuide.Id, 0];
            genInfo.NegCond = [addGuide.Id, 1];
            g.CurrentMedia = g.CurrentMedia.WithPath(
                [addGuide.Id, 2],
                WGNodeData.DT_LATENT_VIDEO,
                genInfo.Model.Compat);
        }
    }

    private WGNodeData ApplyStageUpscaleIfNeeded(
        JsonParser.StageSpec stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData stageLatent,
        WGNodeData sourceMedia)
    {
        if (stage.Upscale == 1 || string.IsNullOrWhiteSpace(stage.UpscaleMethod))
        {
            return stageLatent;
        }

        int baseWidth = Math.Max(sourceMedia?.Width ?? g.UserInput.GetImageWidth(), 16);
        int baseHeight = Math.Max(sourceMedia?.Height ?? g.UserInput.GetImageHeight(), 16);
        (int width, int height) = GetUpscaledDimensions(baseWidth, baseHeight, stage.Upscale);

        if (stage.UpscaleMethod.StartsWith("latentmodel-", StringComparison.OrdinalIgnoreCase))
        {
            string modelName = stage.UpscaleMethod["latentmodel-".Length..];
            return ApplyLatentModelUpscale(genInfo, stageLatent, modelName, width, height);
        }

        if (stage.UpscaleMethod.StartsWith("latent-", StringComparison.OrdinalIgnoreCase))
        {
            string latentMethod = stage.UpscaleMethod["latent-".Length..];
            return ApplyLatentUpscale(stageLatent, latentMethod, stage.Upscale, width, height);
        }

        Logs.Warning(
            $"VideoStages: Stage {stage.Id} uses unsupported LTX upscale method '{stage.UpscaleMethod}'. "
            + "Ignoring upscale.");
        return stageLatent;
    }

    private static (int Width, int Height) GetUpscaledDimensions(int baseWidth, int baseHeight, double upscale)
    {
        int width = AlignTo16((int)Math.Round(baseWidth * upscale));
        int height = AlignTo16((int)Math.Round(baseHeight * upscale));
        return (width, height);
    }

    private static int AlignTo16(int value)
    {
        return Math.Max(16, (Math.Max(value, 16) / 16) * 16);
    }

    private WGNodeData ApplyLatentModelUpscale(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData stageLatent,
        string modelName,
        int width,
        int height)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LatentUpscaleModelLoaderNode loader = bridge.AddNode(new LatentUpscaleModelLoaderNode());
        loader.ModelName.Set(modelName);
        bridge.SyncNode(loader);

        LTXVLatentUpsamplerNode upsampler = bridge.AddNode(new LTXVLatentUpsamplerNode());
        if (genInfo.Vae?.Path is JArray vaePath && bridge.ResolvePath(vaePath) is INodeOutput vae) { upsampler.Vae.ConnectToUntyped(vae); }
        if (bridge.ResolvePath(stageLatent.Path) is INodeOutput samples) { upsampler.Samples.ConnectToUntyped(samples); }
        upsampler.UpscaleModel.ConnectTo(loader.LATENTUPSCALEMODEL);
        bridge.SyncNode(upsampler);
        BridgeSync.SyncLastId(g);

        WGNodeData upscaled = stageLatent.WithPath([upsampler.Id, 0], WGNodeData.DT_LATENT_VIDEO);
        upscaled.Width = width;
        upscaled.Height = height;
        return upscaled;
    }

    private WGNodeData ApplyLatentUpscale(
        WGNodeData stageLatent,
        string latentMethod,
        double scaleBy,
        int width,
        int height)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LatentUpscaleByNode node = bridge.AddNode(new LatentUpscaleByNode());
        if (bridge.ResolvePath(stageLatent.Path) is INodeOutput samples) { node.Samples.ConnectToUntyped(samples); }
        node.UpscaleMethod.Set(latentMethod);
        node.ScaleBy.Set(scaleBy);
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);

        WGNodeData upscaled = stageLatent.WithPath([node.Id, 0], WGNodeData.DT_LATENT_VIDEO);
        upscaled.Width = width;
        upscaled.Height = height;
        return upscaled;
    }

    private WGNodeData BuildStageLatent(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        JsonParser.StageSpec stage,
        WGNodeData sourceMedia,
        LtxPostVideoChain postVideoChain)
    {
        genInfo.StartStep = (int)Math.Floor(stage.Steps * (1 - stage.Control));
        JArray controlNetLengthFrames = TryResolveControlNetLengthFrames(stage);

        if (rootVideoStageTakeover.ShouldReplaceTextToVideoRootStage(stage))
        {
            return CreateEmptyVideoLatent(genInfo, stage, sourceMedia, controlNetLengthFrames);
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
                stage,
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
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode());
        if (imagePath is { Count: 2 } && bridge.ResolvePath(imagePath) is INodeOutput src) { node.Image.ConnectToUntyped(src); }
        node.BatchIndex.Set(batchIndex);
        SetIntInputFromToken(node.Length, length, bridge);
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);
        return node.Id;
    }

    private static void SetIntInputFromToken(NodeInput<IntType> input, JToken token, WorkflowBridge bridge)
    {
        if (token is JArray { Count: 2 } arr && bridge.ResolvePath(arr) is INodeOutput connection)
        {
            input.ConnectToUntyped(connection);
            return;
        }
        if (token is JValue v && v.Value is not null)
        {
            input.Set(System.Convert.ToInt64(v.Value));
        }
    }

    private WGNodeData CreateEmptyVideoLatent(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        JsonParser.StageSpec stage,
        WGNodeData sourceMedia,
        JArray controlNetLengthFrames = null)
    {
        int width = Math.Max(sourceMedia?.Width ?? g.UserInput.GetImageWidth(), 16);
        int height = Math.Max(sourceMedia?.Height ?? g.UserInput.GetImageHeight(), 16);
        int frames = genInfo.Frames ?? sourceMedia?.Frames ?? DefaultFrameCount;
        WGNodeData attachedAudio = sourceMedia?.AttachedAudio;
        return CreateEmptyVideoLatentWithOptionalAudioLength(
            stage,
            genInfo,
            sourceMedia,
            width,
            height,
            frames,
            attachedAudio,
            controlNetLengthFrames);
    }

    private WGNodeData CreateEmptyVideoLatentWithOptionalAudioLength(
        JsonParser.StageSpec stage,
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
            && ShouldMatchStageLengthToAudio(stage)
            && effectiveAttached?.Path is JToken audioPath)
        {
            JToken lengthFramesAudioSource = LtxAudioPathResolution.ResolveLengthToFramesAudioSource(
                bridge,
                audioPath,
                null);

            SwarmAudioLengthToFramesNode lengthToFrames = bridge.AddNode(new SwarmAudioLengthToFramesNode());
            if (lengthFramesAudioSource is JArray audioSourceArr
                && bridge.ResolvePath(audioSourceArr) is INodeOutput audioSrc)
            {
                lengthToFrames.AudioInput.ConnectToUntyped(audioSrc);
            }
            lengthToFrames.FrameRate.Set(fps);
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
        emptyNode.Width.Set(width);
        emptyNode.Height.Set(height);
        SetIntInputFromToken(emptyNode.Length, latentLength, bridge);
        emptyNode.BatchSize.Set(1L);
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
        if (bridge.Graph.GetNode<LTXVEmptyLatentAudioNode>($"{audioPath[0]}") is not LTXVEmptyLatentAudioNode emptyAudio)
        {
            return;
        }
        if (framesConnection is JArray { Count: 2 } framesArr
            && bridge.ResolvePath(framesArr) is INodeOutput framesOutput)
        {
            emptyAudio.FramesNumber.ConnectToUntyped(framesOutput);
        }
        emptyAudio.FrameRate.Set(frameRate);
        bridge.SyncNode(emptyAudio);
    }

    private JArray TryResolveControlNetLengthFrames(JsonParser.StageSpec stage)
    {
        if (stage?.ClipLengthFromControlNet != true)
        {
            return null;
        }
        return ControlNetApplicator.TryCreateCapturedControlImageFrameCount(
            g,
            stage.ClipControlNetSource,
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

    private static bool ShouldMatchStageLengthToAudio(JsonParser.StageSpec stage)
    {
        if (!stage.ClipLengthFromAudio)
        {
            return false;
        }
        if (StringUtils.Equals(stage.ClipAudioSource, Constants.AudioSourceUpload))
        {
            return true;
        }
        return AudioStageDetector.TryParseAceStepFunAudioSource(stage.ClipAudioSource, out _);
    }

    private void ApplyResolvedFpsToWorkflow(WorkflowGenerator.ImageToVideoGenInfo genInfo, int fps)
    {
        if (fps <= 0)
        {
            return;
        }
        genInfo.VideoFPS = fps;
        g.UserInput.Set(T2IParamTypes.VideoFPS, fps, genInfo.ContextID);
    }

    private int ResolveFps(WorkflowGenerator.ImageToVideoGenInfo genInfo, WGNodeData sourceMedia)
    {
        int? fps = genInfo.VideoFPS ?? sourceMedia.FPS;
        if (fps.HasValue && fps.Value > 0)
        {
            return fps.Value;
        }
        fps = jsonParser.ResolveFps();
        return fps.HasValue && fps.Value > 0 ? fps.Value : DefaultFps;
    }

    private static bool ReferencesCurrentOutputPath(WGNodeData media, LtxPostVideoChain postVideoChain)
    {
        if (media?.Path is not JArray mediaPath || postVideoChain is null)
        {
            return false;
        }

        return JToken.DeepEquals(mediaPath, postVideoChain.CurrentOutputMedia?.Path)
            || JToken.DeepEquals(mediaPath, postVideoChain.DecodeOutputPath);
    }

    private JArray ResolvePreprocessedGuidePath(JArray guideImagePath, WGNodeData targetMedia)
    {
        JArray scaledGuidePath = EnsureClipResolutionBeforeLtxvPreprocess(guideImagePath, targetMedia);
        if (TryFindReusablePreprocessOutput(scaledGuidePath, out JArray reusedPath))
        {
            return reusedPath;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LTXVPreprocessNode preprocess = bridge.AddNode(new LTXVPreprocessNode());
        if (scaledGuidePath is { Count: 2 } && bridge.ResolvePath(scaledGuidePath) is INodeOutput src)
        {
            preprocess.Image.ConnectToUntyped(src);
        }
        preprocess.ImgCompression.Set(ImgCompression);
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
        if (TryGetExistingScaleAtTargetDimensions(bridge, guideImagePath, targetW, targetH, out ImageScaleNode existing))
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

        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode());
        if (scaleSourcePath is { Count: 2 } && bridge.ResolvePath(scaleSourcePath) is INodeOutput src)
        {
            scale.Image.ConnectToUntyped(src);
        }
        scale.Width.Set(targetW);
        scale.Height.Set(targetH);
        scale.UpscaleMethod.Set("lanczos");
        scale.Crop.Set("center");
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

    private static bool TryResolveReusablePreprocessNode(WorkflowBridge bridge, JArray imagePath, out string preprocessNodeId)
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

    private void ExecuteSampler(WorkflowGenerator.ImageToVideoGenInfo genInfo)
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
        audioMaskResizer.ApplyCurrentAudioMaskDimensions(g.CurrentMedia);
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

        if (_needsLtxvCropGuidesAfterSampler)
        {
            CropGuidesAfterSampler(genInfo);
            _needsLtxvCropGuidesAfterSampler = false;
        }

        if (genInfo.DoFirstFrameLatentSwap is not null)
        {
            ApplyFirstFrameLatentSwap(genInfo);
        }
    }

    private void ApplyFirstFrameLatentSwap(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ReplaceVideoLatentFramesNode replace = bridge.AddNode(new ReplaceVideoLatentFramesNode());
        if (g.CurrentMedia?.Path is JArray destPath && bridge.ResolvePath(destPath) is INodeOutput dst)
        {
            replace.Destination.ConnectToUntyped(dst);
        }
        if (genInfo.DoFirstFrameLatentSwap is JArray sourcePath && bridge.ResolvePath(sourcePath) is INodeOutput src)
        {
            replace.Source.ConnectToUntyped(src);
        }
        replace.Index.Set(0L);
        bridge.SyncNode(replace);

        NormalizeVideoLatentStartNode normalize = bridge.AddNode(new NormalizeVideoLatentStartNode());
        normalize.LatentInput.ConnectTo(replace.LATENT);
        normalize.StartFrameCount.Set(4L);
        normalize.ReferenceFrameCount.Set(5L);
        bridge.SyncNode(normalize);
        BridgeSync.SyncLastId(g);

        g.CurrentMedia = g.CurrentMedia.WithPath([normalize.Id, 0]);
    }

    private void CropGuidesAfterSampler(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        bool shouldRestoreAudioVideoLatent = g.CurrentMedia.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO;

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput cropLatentSource = null;
        INodeOutput audioLatentSource = null;
        if (shouldRestoreAudioVideoLatent)
        {
            LTXVSeparateAVLatentNode separate = bridge.AddNode(new LTXVSeparateAVLatentNode());
            if (g.CurrentMedia?.Path is JArray avPath && bridge.ResolvePath(avPath) is INodeOutput avSrc)
            {
                separate.AvLatent.ConnectToUntyped(avSrc);
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
        if (bridge.ResolvePath(genInfo.PosCond) is INodeOutput pos) { crop.PositiveInput.ConnectToUntyped(pos); }
        if (bridge.ResolvePath(genInfo.NegCond) is INodeOutput neg) { crop.NegativeInput.ConnectToUntyped(neg); }
        if (cropLatentSource is not null) { crop.LatentInput.ConnectToUntyped(cropLatentSource); }
        bridge.SyncNode(crop);

        genInfo.PosCond = [crop.Id, 0];
        genInfo.NegCond = [crop.Id, 1];

        if (shouldRestoreAudioVideoLatent)
        {
            LTXVConcatAVLatentNode concat = bridge.AddNode(new LTXVConcatAVLatentNode());
            concat.VideoLatent.ConnectTo(crop.Latent);
            if (audioLatentSource is not null) { concat.AudioLatent.ConnectToUntyped(audioLatentSource); }
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
        WGNodeData sourceMedia,
        LtxPostVideoChain postVideoChain)
    {
        int outputWidth = g.CurrentMedia?.Width ?? sourceMedia.Width ?? g.UserInput.GetImageWidth();
        int outputHeight = g.CurrentMedia?.Height ?? sourceMedia.Height ?? g.UserInput.GetImageHeight();
        bool splicedIntoNativeChain = postVideoChain is not null;
        bool parallelMultiClip =
            g.NodeHelpers.TryGetValue(MultiClipParallelMerger.NodeHelperKey, out string parallelFlag)
            && StringUtils.Equals(parallelFlag, "1");
        if (splicedIntoNativeChain)
        {
            if (parallelMultiClip)
            {
                postVideoChain.SpliceCurrentOutputToDedicatedBranch(
                    genInfo.Vae,
                    outputWidth,
                    outputHeight,
                    genInfo.Frames,
                    genInfo.VideoFPS);
            }
            else
            {
                postVideoChain.SpliceCurrentOutput(genInfo.Vae);
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
                postVideoChain.RetargetAnimationSaves(g.CurrentMedia.Path);
            }
            ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, genInfo.Frames, genInfo.VideoFPS);
        }

        g.CurrentVae = genInfo.Vae;
    }

    private string AddSwarmTrimFrames(JArray imagePath, int trimStart, int trimEnd)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        SwarmTrimFramesNode node = bridge.AddNode(new SwarmTrimFramesNode());
        if (imagePath is { Count: 2 } && bridge.ResolvePath(imagePath) is INodeOutput src)
        {
            node.Image.ConnectToUntyped(src);
        }
        node.TrimStart.Set(trimStart);
        node.TrimEnd.Set(trimEnd);
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
