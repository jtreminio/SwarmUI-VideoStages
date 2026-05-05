using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using VideoStages.LTX2;
using VideoStages.WAN;

namespace VideoStages;

internal class StageRunner(
    WorkflowGenerator g,
    StageGuideMediaHelper stageGuideMediaHelper,
    LtxManager ltxManager,
    Base2EditPublishedStageRefs base2EditPublishedStageRefs)
{
    private sealed record StageGenerationPlan(
        WorkflowGenerator.ImageToVideoGenInfo GenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> ApplySourceVideoLatent);

    private sealed record WanRefConditioning(
        int ClipId,
        JArray Positive,
        JArray Negative);

    private WanRefConditioning lastWanRefConditioning;

    public void RunStage(
        JsonParser.StageSpec stage,
        int sectionId,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore)
    {
        if (g.CurrentMedia is null)
        {
            Logs.Error($"VideoStages: Stage {stage.Id} has no input media available.");
            return;
        }

        PromptParser.LoraOverrideScope loraScope = PromptParser.ApplyLoraScope(
            g.UserInput,
            stage.ClipId,
            sectionId);

        try
        {
            JArray priorOutputPath = CopyPath(g.CurrentMedia.Path);
            ltxManager.PrepareReusableAudio(stage);
            bool replaceTextToVideoRootStage = stage.ClipStageIndex == 0
                && RootVideoStageTakeover.IsTextToVideoRootWorkflow(g);
            LtxPostVideoChain postVideoChain = replaceTextToVideoRootStage
                ? null
                : ltxManager.TryCapturePostVideoChain(stage);
            WGNodeData sourceMedia = replaceTextToVideoRootStage
                ? CloneMedia(g.CurrentMedia)
                : ApplyStageUpscaleIfNeeded(stage, sectionId);
            if (sourceMedia is null)
            {
                Logs.Error($"VideoStages: Stage {stage.Id} could not resolve source media.");
                return;
            }
            StageGenerationPlan generationPlan = BuildGenInfo(stage, sectionId, sourceMedia);
            if (generationPlan is null)
            {
                return;
            }
            WorkflowGenerator.ImageToVideoGenInfo genInfo = generationPlan.GenInfo;
            Action<WorkflowGenerator.ImageToVideoGenInfo> controlNetPostHandler = ScopedImageToVideoPostHandler(
                genInfo,
                currentGenInfo =>
                {
                    ApplyControlNetLora(stage, currentGenInfo);
                    ControlNetApplicator.Apply(
                        g,
                        currentGenInfo,
                        sourceMedia,
                        stage.ClipControlNetSource,
                        stage.ControlNetStrength,
                        stage.ClipLengthFromControlNet);
                });
            WorkflowGenerator.AltImageToVideoPostHandlers.Add(controlNetPostHandler);
            try
            {
                if (ltxManager.TryRunLocalStage(
                        stage,
                        guideReference,
                        refStore,
                        genInfo,
                        generationPlan.ApplySourceVideoLatent,
                        sourceMedia,
                        priorOutputPath,
                        postVideoChain))
                {
                    RetargetExistingAnimationSaves(
                        priorOutputPath,
                        g.CurrentMedia?.Path,
                        retargetAudio: g.CurrentMedia?.AttachedAudio is not null);
                    CleanupReplacedTextToVideoRootStage(priorOutputPath, replaceTextToVideoRootStage);
                    return;
                }

                WGNodeData guideRaw = stageGuideMediaHelper.ResolveGuideMedia(guideReference, postVideoChain);
                WanStageReferenceHandler.WanGuideResolution wanRefs = WanStageReferenceHandler.TryResolveClipRefs(
                    g,
                    stageGuideMediaHelper,
                    base2EditPublishedStageRefs,
                    stage,
                    refStore,
                    postVideoChain);
                if (wanRefs.StartRaw is not null)
                {
                    guideRaw = wanRefs.StartRaw;
                }

                WGNodeData guideMedia = stageGuideMediaHelper.PrepareGuideMedia(
                    guideRaw,
                    sourceMedia,
                    scaleToSourceSize: true);
                WGNodeData wanEndPrepared = null;
                if (wanRefs.EndRaw is not null)
                {
                    wanEndPrepared = stageGuideMediaHelper.PrepareGuideMedia(
                        wanRefs.EndRaw,
                        sourceMedia,
                        scaleToSourceSize: false);
                }

                RunNativeStage(stage, generationPlan, sourceMedia, guideMedia, priorOutputPath, wanEndPrepared);
                CleanupReplacedTextToVideoRootStage(priorOutputPath, replaceTextToVideoRootStage);
            }
            finally
            {
                _ = WorkflowGenerator.AltImageToVideoPostHandlers.Remove(controlNetPostHandler);
            }
        }
        finally
        {
            loraScope?.Dispose();
        }
    }

    private void RunNativeStage(
        JsonParser.StageSpec stage,
        StageGenerationPlan generationPlan,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        JArray priorOutputPath,
        WGNodeData wanEndPrepared)
    {
        WorkflowGenerator.ImageToVideoGenInfo genInfo = generationPlan.GenInfo;
        g.CurrentMedia = guideMedia ?? sourceMedia;
        Action<WorkflowGenerator.ImageToVideoGenInfo> postHandler = null;
        Action<WorkflowGenerator.ImageToVideoGenInfo> wanPreHandler = ScopedImageToVideoPreHandler(
            genInfo,
            currentGenInfo => ApplyWanStillImageMediaLenBypass(currentGenInfo, stage, sourceMedia));
        Action<WorkflowGenerator.ImageToVideoGenInfo> wanPostHandler = ScopedImageToVideoPostHandler(
            genInfo,
            currentGenInfo =>
            {
                CollapseWanStartImageScaleChain(currentGenInfo);
                WanFirstLastFrameRewriter.TryRewriteToFirstLast(g, stage, currentGenInfo, wanEndPrepared);
                ApplyWanRefConditioningReuse(stage, currentGenInfo);
            });
        if (generationPlan.ApplySourceVideoLatent is not null)
        {
            postHandler = ScopedImageToVideoPostHandler(genInfo, generationPlan.ApplySourceVideoLatent);
            WorkflowGenerator.AltImageToVideoPostHandlers.Add(postHandler);
        }

        WorkflowGenerator.AltImageToVideoPreHandlers.Add(wanPreHandler);
        WorkflowGenerator.AltImageToVideoPostHandlers.Add(wanPostHandler);
        try
        {
            g.CreateImageToVideo(genInfo);
            ApplyContinuationEndStep(stage);
        }
        finally
        {
            _ = WorkflowGenerator.AltImageToVideoPostHandlers.Remove(wanPostHandler);
            _ = WorkflowGenerator.AltImageToVideoPreHandlers.Remove(wanPreHandler);
            if (postHandler is not null)
            {
                _ = WorkflowGenerator.AltImageToVideoPostHandlers.Remove(postHandler);
            }
        }
        g.CurrentVae = genInfo.Vae;
        StampCurrentMediaMetadata(sourceMedia, genInfo);
        RetargetExistingAnimationSaves(priorOutputPath, g.CurrentMedia?.Path);
    }

    private void ApplyWanRefConditioningReuse(
        JsonParser.StageSpec stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!ShouldReuseWanRefConditioning(stage, genInfo))
        {
            if (stage.ClipStageIndex == 0)
            {
                lastWanRefConditioning = null;
            }
            return;
        }

        if (stage.ClipStageIndex == 0)
        {
            lastWanRefConditioning = new WanRefConditioning(
                stage.ClipId,
                CopyPath(genInfo.PosCond),
                CopyPath(genInfo.NegCond));
            return;
        }

        if (lastWanRefConditioning is null
            || lastWanRefConditioning.ClipId != stage.ClipId
            || lastWanRefConditioning.Positive is null
            || lastWanRefConditioning.Negative is null)
        {
            return;
        }

        JArray stalePositive = CopyPath(genInfo.PosCond);
        JArray staleNegative = CopyPath(genInfo.NegCond);
        genInfo.PosCond = CopyPath(lastWanRefConditioning.Positive);
        genInfo.NegCond = CopyPath(lastWanRefConditioning.Negative);
        RemoveUnusedWanRefConditioning(stalePositive, staleNegative);
    }

    private static bool ShouldReuseWanRefConditioning(
        JsonParser.StageSpec stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        return VideoStageModelCompat.SupportsWanFirstLastFrame(genInfo.VideoModel)
            && genInfo.PosCond is { Count: 2 }
            && genInfo.NegCond is { Count: 2 };
    }

    private void ApplyContinuationEndStep(JsonParser.StageSpec stage)
    {
        if (!stage.EndStep.HasValue || g.CurrentMedia?.Path is not JArray { Count: 2 } currentPath)
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.Graph.GetNode($"{currentPath[0]}") is not ComfyNode startNode)
        {
            return;
        }
        ComfyNode samplerNode = FindUpstreamSampler(bridge, startNode);
        if (samplerNode is null)
        {
            return;
        }

        switch (samplerNode)
        {
            case KSamplerAdvancedNode advanced:
                {
                    int steps = Math.Max(1, advanced.Steps.LiteralAsInt() ?? stage.Steps);
                    int endStep = Math.Clamp(stage.EndStep.Value, 0, steps);
                    advanced.EndAtStep.Set(endStep);
                    advanced.ReturnWithLeftoverNoise.Set(endStep < steps ? "enable" : "disable");
                    bridge.SyncNode(advanced);
                    break;
                }
            case SwarmKSamplerNode swarm:
                {
                    int steps = Math.Max(1, swarm.Steps.LiteralAsInt() ?? stage.Steps);
                    int endStep = Math.Clamp(stage.EndStep.Value, 0, steps);
                    swarm.EndAtStep.Set(endStep);
                    swarm.ReturnWithLeftoverNoise.Set(endStep < steps ? "enable" : "disable");
                    bridge.SyncNode(swarm);
                    break;
                }
        }
    }

    private static ComfyNode FindUpstreamSampler(WorkflowBridge bridge, ComfyNode startNode)
    {
        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(startNode);
        visited.Add(startNode.Id);

        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            if (current is KSamplerAdvancedNode or SwarmKSamplerNode)
            {
                return current;
            }
            foreach (INodeInput input in current.Inputs)
            {
                if (input.Connection?.Node is ComfyNode upstream && visited.Add(upstream.Id))
                {
                    pending.Enqueue(upstream);
                }
            }
        }
        return null;
    }

    private void RemoveUnusedWanRefConditioning(JArray stalePositive, JArray staleNegative)
    {
        if (stalePositive is null)
        {
            return;
        }

        HashSet<string> protectedNodes = [];
        AddCurrentMediaRootNodeId(protectedNodes, g.CurrentMedia);
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        RemoveUnusedUpstreamNodes(bridge, $"{stalePositive[0]}", protectedNodes);
        if (staleNegative is not null && !JToken.DeepEquals(staleNegative, stalePositive))
        {
            RemoveUnusedUpstreamNodes(bridge, $"{staleNegative[0]}", protectedNodes);
        }
    }

    private StageGenerationPlan BuildGenInfo(
        JsonParser.StageSpec stage,
        int sectionId,
        WGNodeData sourceMedia)
    {
        T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null, sectionId: sectionId);
        if (videoModel is null)
        {
            Logs.Error($"VideoStages: Stage {stage.Id} could not resolve video model '{stage.Model}'.");
            return null;
        }
        _ = g.NodeHelpers.Remove($"modelloader_{videoModel.Name}_image2video");

        bool sourceIsVideo = sourceMedia.DataType == WGNodeData.DT_VIDEO;
        bool shouldUseLocalLtxv2Path = VideoStageModelCompat.IsLtxV2VideoModel(videoModel)
            && sourceIsVideo;
        int? frames = sourceMedia.Frames;
        if (!frames.HasValue
            && g.UserInput.TryGet(T2IParamTypes.VideoFrames, out int explicitFrames, sectionId: sectionId))
        {
            frames = explicitFrames;
        }
        if (!frames.HasValue
            && g.UserInput.TryGet(
                T2IParamTypes.Text2VideoFrames,
                out int textToVideoFrames,
                sectionId: sectionId))
        {
            frames = textToVideoFrames;
        }

        int? fps = sourceMedia.FPS;
        if (!fps.HasValue && g.UserInput.TryGet(T2IParamTypes.VideoFPS, out int explicitFps, sectionId: sectionId))
        {
            fps = explicitFps;
        }

        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent = null;
        int batchIndex = -1;
        int batchLen = -1;
        bool isWanStage = VideoStageModelCompat.IsWanVideoModel(videoModel);
        if (sourceIsVideo)
        {
            batchIndex = 0;
            batchLen = 1;
            if (!shouldUseLocalLtxv2Path)
            {
                applySourceVideoLatent = genInfo =>
                {
                    if (!genInfo.Frames.HasValue)
                    {
                        return;
                    }
                    if (isWanStage && TryReuseDecodedSourceVideoLatent(sourceMedia, genInfo.Vae, out WGNodeData reusedLatent))
                    {
                        genInfo.StartStep = (int)Math.Floor(stage.Steps * (1 - stage.Control));
                        g.CurrentMedia = reusedLatent;
                        return;
                    }
                    string fromBatch = AddImageFromBatch(sourceMedia.Path, batchIndex: 0, length: genInfo.Frames.Value);
                    genInfo.StartStep = (int)Math.Floor(stage.Steps * (1 - stage.Control));
                    g.CurrentMedia = sourceMedia.WithPath([fromBatch, 0]);
                    g.CurrentMedia.Frames = Math.Min(genInfo.Frames.Value, g.CurrentMedia.Frames ?? int.MaxValue);
                    g.CurrentMedia = g.CurrentMedia.AsLatentImage(genInfo.Vae);
                };
            }
        }

        string positivePrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negativePrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        string originalPositivePrompt = PromptParser.GetOriginalPrompt(
            g.UserInput,
            T2IParamTypes.Prompt.Type.ID,
            positivePrompt);
        string originalNegativePrompt = PromptParser.GetOriginalPrompt(
            g.UserInput,
            T2IParamTypes.NegativePrompt.Type.ID,
            negativePrompt);

        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = g,
            VideoModel = videoModel,
            VideoSwapModel = null,
            VideoSwapPercent = 0.5,
            Frames = frames,
            VideoCFG = stage.CfgScale,
            VideoFPS = fps,
            Width = sourceMedia.Width ?? g.UserInput.GetImageWidth(),
            Height = sourceMedia.Height ?? g.UserInput.GetImageHeight(),
            Prompt = PromptParser.ExtractPrompt(
                positivePrompt,
                originalPositivePrompt,
                stage.ClipId,
                stage.Id,
                stage.ClipStageIndex),
            NegativePrompt = PromptParser.ExtractPrompt(
                negativePrompt,
                originalNegativePrompt,
                stage.ClipId,
                stage.Id,
                stage.ClipStageIndex),
            Steps = stage.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.Id,
            BatchIndex = batchIndex,
            BatchLen = batchLen,
            ContextID = sectionId,
            VideoEndFrame = g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
        };
        return new StageGenerationPlan(genInfo, applySourceVideoLatent);
    }

    private bool TryReuseDecodedSourceVideoLatent(
        WGNodeData sourceMedia,
        WGNodeData vae,
        out WGNodeData reusedLatent)
    {
        reusedLatent = null;
        if (sourceMedia?.Path is not JArray { Count: 2 } sourcePath
            || vae?.Path is not JArray { Count: 2 } vaePath)
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
        if (samplesConn is null
            || decodeVaeConn is null
            || decodeVaeConn.Node.Id != $"{vaePath[0]}"
            || decodeVaeConn.SlotIndex != (int)vaePath[1])
        {
            return false;
        }

        reusedLatent = sourceMedia.WithPath(
            new JArray(samplesConn.Node.Id, samplesConn.SlotIndex),
            WGNodeData.DT_LATENT_VIDEO,
            vae.Compat);
        return true;
    }

    private string AddImageFromBatch(JArray imagePath, int batchIndex, int length)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode());
        if (imagePath is { Count: 2 } && bridge.ResolvePath(imagePath) is INodeOutput src)
        {
            node.Image.ConnectToUntyped(src);
        }
        node.BatchIndex.Set(batchIndex);
        node.Length.Set(length);
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);
        return node.Id;
    }

    private static Action<WorkflowGenerator.ImageToVideoGenInfo> ScopedImageToVideoPreHandler(
        WorkflowGenerator.ImageToVideoGenInfo expectedGenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> action)
    {
        return currentGenInfo =>
        {
            if (!ReferenceEquals(currentGenInfo, expectedGenInfo))
            {
                return;
            }
            action(currentGenInfo);
        };
    }

    private static Action<WorkflowGenerator.ImageToVideoGenInfo> ScopedImageToVideoPostHandler(
        WorkflowGenerator.ImageToVideoGenInfo expectedGenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> action)
    {
        return currentGenInfo =>
        {
            if (!ReferenceEquals(currentGenInfo, expectedGenInfo))
            {
                return;
            }
            action(currentGenInfo);
        };
    }

    private void CollapseWanStartImageScaleChain(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!VideoStageModelCompat.SupportsWanFirstLastFrame(genInfo.VideoModel)
            || genInfo.PosCond is not { Count: >= 2 })
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.Graph.GetNode<WanImageToVideoNode>($"{genInfo.PosCond[0]}") is not WanImageToVideoNode wan
            || wan.StartImage.Connection?.Node is not ImageScaleNode startScale
            || startScale.Image.Connection?.Node is not ImageScaleNode upstreamScale)
        {
            return;
        }

        upstreamScale.Width.Set(startScale.Width.LiteralAsLong() ?? 0L);
        upstreamScale.Height.Set(startScale.Height.LiteralAsLong() ?? 0L);
        upstreamScale.Crop.Set(startScale.Crop.LiteralAsString() ?? "center");
        upstreamScale.UpscaleMethod.Set(startScale.UpscaleMethod.LiteralAsString() ?? "lanczos");
        wan.StartImage.ConnectTo(upstreamScale.IMAGE);
        bridge.SyncNode(upstreamScale);
        bridge.SyncNode(wan);

        if (!bridge.Graph.FindInputsConnectedTo(startScale.IMAGE).Any())
        {
            bridge.RemoveNode(startScale);
        }
    }

    private static void ApplyWanStillImageMediaLenBypass(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        JsonParser.StageSpec stage,
        WGNodeData sourceMedia)
    {
        WorkflowGenerator workflowGenerator = genInfo.Generator;
        if (!VideoStageModelCompat.SupportsWanFirstLastFrame(genInfo.VideoModel)
            || stage.ClipStageIndex != 0
            || sourceMedia.DataType == WGNodeData.DT_VIDEO
            || workflowGenerator.CurrentMedia is null
            || workflowGenerator.CurrentMedia.DataType == WGNodeData.DT_VIDEO)
        {
            return;
        }

        genInfo.HasFixedMediaLen = true;
    }

    private void ApplyControlNetLora(JsonParser.StageSpec stage, WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (string.IsNullOrWhiteSpace(stage.ClipControlNetLora) || genInfo.Model is null)
        {
            return;
        }

        if (!VideoStageModelCompat.IsLtxV2VideoModel(genInfo.VideoModel))
        {
            return;
        }

        T2IModel lora = ResolveLoraModel(stage.ClipControlNetLora);
        if (lora is null)
        {
            return;
        }
        if (!g.Features.Contains(Constants.LtxVideoFeatureFlag))
        {
            throw new SwarmUserErrorException(
                "VideoStages ControlNet LoRA requires the ComfyUI-LTXVideo custom nodes. "
                + $"Install {Constants.LtxVideoNodeUrl} or use SwarmUI's LTXVideo feature installer.");
        }
        g.FinalLoadedModelList.Add(lora);
        if (Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash)
        {
            lora.GetOrGenerateTensorHashSha256();
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        LTXICLoRALoaderModelOnlyNode loraLoader = bridge.AddNode(new LTXICLoRALoaderModelOnlyNode());
        if (genInfo.Model?.Path is JArray modelPath && bridge.ResolvePath(modelPath) is INodeOutput modelOutput)
        {
            loraLoader.ModelInput.ConnectToUntyped(modelOutput);
        }
        loraLoader.LoraName.Set(lora.ToString(g.ModelFolderFormat));
        loraLoader.StrengthModel.Set(1.0);
        bridge.SyncNode(loraLoader);
        BridgeSync.SyncLastId(g);
        genInfo.Model = genInfo.Model.WithPath([loraLoader.Id, 0]);
    }

    private static T2IModel ResolveLoraModel(string loraName)
    {
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler loraHandler))
        {
            Logs.Error("LoRA models are not available.");
            return null;
        }
        if (!loraHandler.Models.TryGetValue(loraName + ".safetensors", out T2IModel lora)
            && !loraHandler.Models.TryGetValue(loraName, out lora))
        {
            Logs.Error($"LoRA Model '{loraName}' not found in the model set.");
            return null;
        }
        return lora;
    }

    private WGNodeData ApplyStageUpscaleIfNeeded(JsonParser.StageSpec stage, int sectionId)
    {
        WGNodeData source = VaeDecodePreference.AsRawImage(g, g.CurrentMedia, g.CurrentVae);
        int width = Math.Max(source.Width ?? g.UserInput.GetImageWidth(), 16);
        int height = Math.Max(source.Height ?? g.UserInput.GetImageHeight(), 16);
        source.Width = width;
        source.Height = height;

        if (stage.Upscale == 1 || string.IsNullOrWhiteSpace(stage.UpscaleMethod))
        {
            g.CurrentMedia = source;
            return source;
        }

        int targetWidth = Math.Max(16, (int)Math.Round(width * stage.Upscale));
        int targetHeight = Math.Max(16, (int)Math.Round(height * stage.Upscale));
        targetWidth = (targetWidth / 16) * 16;
        targetHeight = (targetHeight / 16) * 16;

        T2IModel stageVideoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null, sectionId: sectionId);
        bool isLtxv2Stage = VideoStageModelCompat.IsLtxV2VideoModel(stageVideoModel);
        if (source.DataType == WGNodeData.DT_VIDEO && VideoStageModelCompat.IsWanVideoModel(stageVideoModel))
        {
            g.CurrentMedia = source;
            return source;
        }

        if (isLtxv2Stage)
        {
            if (IsSupportedLtxUpscaleMethod(stage.UpscaleMethod))
            {
                g.CurrentMedia = source;
                return source;
            }

            Logs.Warning(
                $"VideoStages: Stage {stage.Id} uses unsupported LTX upscale method "
                + $"'{stage.UpscaleMethod}'. Ignoring upscale.");
            g.CurrentMedia = source;
            return source;
        }

        if (stage.UpscaleMethod.StartsWith("pixel-", StringComparison.OrdinalIgnoreCase))
        {
            string method = stage.UpscaleMethod["pixel-".Length..];
            ImageScaleNode scaleNode = AddDisabledCropImageScale(source.Path, targetWidth, targetHeight, method);
            g.CurrentMedia = source.WithPath([scaleNode.Id, 0]);
            g.CurrentMedia.Width = targetWidth;
            g.CurrentMedia.Height = targetHeight;
            return g.CurrentMedia;
        }

        if (stage.UpscaleMethod.StartsWith("model-", StringComparison.OrdinalIgnoreCase))
        {
            string modelName = stage.UpscaleMethod["model-".Length..];
            string fitScaleId = AddModelUpscaleChain(source.Path, modelName, targetWidth, targetHeight);
            g.CurrentMedia = source.WithPath([fitScaleId, 0]);
            g.CurrentMedia.Width = targetWidth;
            g.CurrentMedia.Height = targetHeight;
            return g.CurrentMedia;
        }

        if (stage.Upscale != 1)
        {
            Logs.Warning(
                $"VideoStages: Stage {stage.Id} uses unsupported upscale method "
                + $"'{stage.UpscaleMethod}'. Ignoring upscale.");
        }

        g.CurrentMedia = source;
        return source;
    }

    private static bool IsSupportedLtxUpscaleMethod(string upscaleMethod)
    {
        return upscaleMethod.StartsWith("latent-", StringComparison.OrdinalIgnoreCase)
            || upscaleMethod.StartsWith("latentmodel-", StringComparison.OrdinalIgnoreCase);
    }

    private ImageScaleNode AddDisabledCropImageScale(JArray sourcePath, int width, int height, string upscaleMethod)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode());
        if (sourcePath is { Count: 2 } && bridge.ResolvePath(sourcePath) is INodeOutput src)
        {
            scale.Image.ConnectToUntyped(src);
        }
        scale.Width.Set(width);
        scale.Height.Set(height);
        scale.UpscaleMethod.Set(upscaleMethod);
        scale.Crop.Set("disabled");
        bridge.SyncNode(scale);
        BridgeSync.SyncLastId(g);
        return scale;
    }

    private string AddModelUpscaleChain(JArray sourcePath, string modelName, int targetWidth, int targetHeight)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        UpscaleModelLoaderNode loader = bridge.AddNode(new UpscaleModelLoaderNode());
        loader.ModelName.Set(modelName);
        bridge.SyncNode(loader);

        ImageUpscaleWithModelNode upscale = bridge.AddNode(new ImageUpscaleWithModelNode());
        upscale.UpscaleModel.ConnectTo(loader.UPSCALEMODEL);
        if (sourcePath is { Count: 2 } && bridge.ResolvePath(sourcePath) is INodeOutput src)
        {
            upscale.Image.ConnectToUntyped(src);
        }
        bridge.SyncNode(upscale);

        ImageScaleNode fit = bridge.AddNode(new ImageScaleNode());
        fit.Image.ConnectTo(upscale.IMAGE);
        fit.Width.Set(targetWidth);
        fit.Height.Set(targetHeight);
        fit.UpscaleMethod.Set("lanczos");
        fit.Crop.Set("disabled");
        bridge.SyncNode(fit);
        BridgeSync.SyncLastId(g);
        return fit.Id;
    }

    private void StampCurrentMediaMetadata(WGNodeData sourceMedia, WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }

        g.CurrentMedia.Width = sourceMedia.Width;
        g.CurrentMedia.Height = sourceMedia.Height;
        g.CurrentMedia.Frames = genInfo.Frames ?? g.CurrentMedia.Frames;
        g.CurrentMedia.FPS = genInfo.VideoFPS ?? g.CurrentMedia.FPS;
    }

    private void RetargetExistingAnimationSaves(
        JArray priorOutputPath,
        JArray newOutputPath,
        bool retargetAudio = false)
    {
        if (priorOutputPath is not { Count: 2 }
            || newOutputPath is not { Count: 2 }
            || JToken.DeepEquals(priorOutputPath, newOutputPath))
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput oldOutput = bridge.ResolvePath(priorOutputPath);
        INodeOutput newOutput = bridge.ResolvePath(newOutputPath);
        if (oldOutput is null || newOutput is null)
        {
            return;
        }

        JArray newAudioPath = retargetAudio ? CopyPath(g.CurrentMedia?.AttachedAudio?.Path) : null;
        INodeOutput newAudioOutput = newAudioPath is not null ? bridge.ResolvePath(newAudioPath) : null;
        Dictionary<string, JArray> staleAudioPaths = [];

        foreach (SwarmSaveAnimationWSNode saveNode in bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>())
        {
            if (saveNode.Images.Connection != oldOutput)
            {
                continue;
            }

            saveNode.Images.ConnectToUntyped(newOutput);
            if (retargetAudio)
            {
                if (saveNode.Audio.Connection is INodeOutput oldAudioOutput)
                {
                    JArray oldAudioPath = WorkflowBridge.ToPath(oldAudioOutput);
                    staleAudioPaths.TryAdd($"{oldAudioPath[0]}:{oldAudioPath[1]}", oldAudioPath);
                }
                if (newAudioOutput is not null)
                {
                    saveNode.Audio.ConnectToUntyped(newAudioOutput);
                }
                else
                {
                    saveNode.Audio.Clear();
                }
            }
            bridge.SyncNode(saveNode);
        }

        HashSet<string> protectedNodes = [];
        AddCurrentMediaRootNodeId(protectedNodes, g.CurrentMedia);
        if (newAudioPath is not null)
        {
            protectedNodes.Add($"{newAudioPath[0]}");
        }
        foreach (JArray staleAudioPath in staleAudioPaths.Values)
        {
            RemoveUnusedUpstreamNodes(bridge, $"{staleAudioPath[0]}", protectedNodes);
        }
        BridgeSync.SyncLastId(g);
    }

    internal static JArray CopyPath(JArray path)
    {
        if (path is not { Count: 2 })
        {
            return null;
        }
        return new JArray(path[0], path[1]);
    }

    private static void AddCurrentMediaRootNodeId(HashSet<string> protectedNodes, WGNodeData media)
    {
        if (media?.Path is not JArray { Count: 2 } currentPath)
        {
            return;
        }
        protectedNodes.Add($"{currentPath[0]}");
    }

    // todo: maybe delete this
    private static void RemoveUnusedUpstreamNodes(
        WorkflowBridge bridge,
        string startNodeId,
        ISet<string> protectedNodeIds = null)
    {
        if (string.IsNullOrWhiteSpace(startNodeId))
        {
            return;
        }

        Queue<string> pending = new();
        HashSet<string> seen = [];
        pending.Enqueue(startNodeId);

        while (pending.Count > 0)
        {
            string nodeId = pending.Dequeue();
            if (string.IsNullOrWhiteSpace(nodeId)
                || !seen.Add(nodeId)
                || protectedNodeIds?.Contains(nodeId) == true)
            {
                continue;
            }

            ComfyNode node = bridge.Graph.GetNode(nodeId);
            if (node is null)
            {
                continue;
            }

            bool hasDownstreamConsumer = false;
            foreach (INodeOutput output in node.Outputs)
            {
                if (bridge.Graph.FindInputsConnectedTo(output).Any())
                {
                    hasDownstreamConsumer = true;
                    break;
                }
            }
            if (hasDownstreamConsumer)
            {
                continue;
            }

            List<string> upstreamIds = [];
            foreach (INodeInput input in node.Inputs)
            {
                string upId = input.Connection?.Node?.Id;
                if (!string.IsNullOrWhiteSpace(upId))
                {
                    upstreamIds.Add(upId);
                }
            }

            bridge.RemoveNode(nodeId);
            foreach (string upId in upstreamIds)
            {
                pending.Enqueue(upId);
            }
        }
    }

    private void CleanupReplacedTextToVideoRootStage(JArray priorOutputPath, bool replaceTextToVideoRootStage)
    {
        if (!replaceTextToVideoRootStage || priorOutputPath is not { Count: 2 })
        {
            return;
        }

        HashSet<string> protectedNodes = [];
        AddCurrentMediaRootNodeId(protectedNodes, g.CurrentMedia);
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        RemoveUnusedUpstreamNodes(bridge, $"{priorOutputPath[0]}", protectedNodes);
    }

    private static WGNodeData CloneMedia(WGNodeData media)
    {
        if (media?.Path is not JArray { Count: 2 } path)
        {
            return null;
        }
        WGNodeData clone = media.WithPath(CopyPath(path), media.DataType, media.Compat);
        if (CopyPath(media.AttachedAudio?.Path) is JArray audioPath)
        {
            clone.AttachedAudio = media.AttachedAudio.WithPath(
                audioPath,
                media.AttachedAudio.DataType,
                media.AttachedAudio.Compat);
        }
        return clone;
    }
}
