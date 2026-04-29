using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
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
            stage.ClipId);

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
        if (!stage.EndStep.HasValue || FindCurrentStageSamplerNode() is not JObject samplerNode)
        {
            return;
        }
        if (samplerNode["inputs"] is not JObject inputs)
        {
            return;
        }

        int steps = Math.Max(1, inputs.Value<int?>("steps") ?? stage.Steps);
        int endStep = Math.Clamp(stage.EndStep.Value, 0, steps);
        inputs["end_at_step"] = endStep;
        inputs["return_with_leftover_noise"] = endStep < steps ? "enable" : "disable";
    }

    private JObject FindCurrentStageSamplerNode()
    {
        if (g.CurrentMedia?.Path is not JArray currentPath || currentPath.Count != 2)
        {
            return null;
        }
        return FindSamplerNodeFromPath(currentPath, []);
    }

    private JObject FindSamplerNodeFromPath(JArray path, HashSet<string> visitedNodeIds)
    {
        if (path is not { Count: 2 })
        {
            return null;
        }

        string nodeId = $"{path[0]}";
        if (!visitedNodeIds.Add(nodeId)
            || g.Workflow[nodeId] is not JObject node)
        {
            return null;
        }

        if (IsSamplerNode(node))
        {
            return node;
        }

        if (node["inputs"] is not JObject inputs)
        {
            return null;
        }

        foreach (JArray upstreamPath in ExtractNodeRefs(inputs))
        {
            JObject samplerNode = FindSamplerNodeFromPath(upstreamPath, visitedNodeIds);
            if (samplerNode is not null)
            {
                return samplerNode;
            }
        }
        return null;
    }

    private static IEnumerable<JArray> ExtractNodeRefs(JToken token)
    {
        if (token is JArray array)
        {
            if (array.Count == 2
                && array[0] is not null
                && array[1] is not null
                && array[0].Type != JTokenType.Array
                && array[1].Type != JTokenType.Array)
            {
                yield return new JArray(array[0], array[1]);
                yield break;
            }

            foreach (JToken child in array)
            {
                foreach (JArray childRef in ExtractNodeRefs(child))
                {
                    yield return childRef;
                }
            }
            yield break;
        }

        if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                foreach (JArray childRef in ExtractNodeRefs(property.Value))
                {
                    yield return childRef;
                }
            }
        }
    }

    private static bool IsSamplerNode(JObject node)
    {
        return StringUtils.NodeTypeMatches(node, "SwarmKSampler")
            || StringUtils.NodeTypeMatches(node, "KSamplerAdvanced");
    }

    private void RemoveUnusedWanRefConditioning(JArray stalePositive, JArray staleNegative)
    {
        if (stalePositive is null)
        {
            return;
        }

        HashSet<string> protectedNodes = [];
        AddCurrentMediaRootNodeId(protectedNodes, g.CurrentMedia);
        WorkflowUtils.RemoveUnusedUpstreamNodes(g.Workflow, stalePositive, protectedNodes);
        if (staleNegative is not null && !JToken.DeepEquals(staleNegative, stalePositive))
        {
            WorkflowUtils.RemoveUnusedUpstreamNodes(g.Workflow, staleNegative, protectedNodes);
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
                    string fromBatch = g.CreateNode(NodeTypes.ImageFromBatch, new JObject()
                    {
                        ["image"] = sourceMedia.Path,
                        ["batch_index"] = 0,
                        ["length"] = genInfo.Frames.Value
                    });
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
            Prompt = PromptParser.ExtractPrompt(positivePrompt, originalPositivePrompt, stage.ClipId),
            NegativePrompt = PromptParser.ExtractPrompt(negativePrompt, originalNegativePrompt, stage.ClipId),
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
        if (sourceMedia?.Path is not JArray sourcePath
            || sourcePath.Count != 2
            || vae?.Path is not JArray vaePath
            || vaePath.Count != 2
            || g.Workflow[$"{sourcePath[0]}"] is not JObject sourceNode
            || (!StringUtils.NodeTypeMatches(sourceNode, NodeTypes.VAEDecode)
                && !StringUtils.NodeTypeMatches(sourceNode, NodeTypes.VAEDecodeTiled))
            || sourceNode["inputs"] is not JObject inputs
            || inputs["samples"] is not JArray samplesPath
            || samplesPath.Count != 2
            || inputs["vae"] is not JArray decodeVaePath
            || decodeVaePath.Count != 2
            || !JToken.DeepEquals(decodeVaePath, vaePath))
        {
            return false;
        }

        reusedLatent = sourceMedia.WithPath(
            new JArray(samplesPath[0], samplesPath[1]),
            WGNodeData.DT_LATENT_VIDEO,
            vae.Compat);
        return true;
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
            || genInfo.PosCond is not { Count: >= 2 }
            || g.Workflow[$"{genInfo.PosCond[0]}"] is not JObject wanNode
            || !StringUtils.NodeTypeMatches(wanNode, "WanImageToVideo")
            || wanNode["inputs"] is not JObject wanInputs
            || wanInputs["start_image"] is not JArray startPath
            || startPath.Count != 2
            || g.Workflow[$"{startPath[0]}"] is not JObject startNode
            || !StringUtils.NodeTypeMatches(startNode, NodeTypes.ImageScale)
            || startNode["inputs"] is not JObject startInputs
            || startInputs["image"] is not JArray upstreamPath
            || upstreamPath.Count != 2
            || g.Workflow[$"{upstreamPath[0]}"] is not JObject upstreamNode
            || !StringUtils.NodeTypeMatches(upstreamNode, NodeTypes.ImageScale)
            || upstreamNode["inputs"] is not JObject upstreamInputs)
        {
            return;
        }

        upstreamInputs["width"] = startInputs["width"];
        upstreamInputs["height"] = startInputs["height"];
        upstreamInputs["crop"] = startInputs["crop"] ?? "center";
        upstreamInputs["upscale_method"] = startInputs["upscale_method"] ?? "lanczos";
        wanInputs["start_image"] = new JArray(upstreamPath[0], upstreamPath[1]);
        if (WorkflowUtils.FindInputConnections(g.Workflow, startPath).Count == 0)
        {
            g.Workflow.Remove($"{startPath[0]}");
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

        string loraLoader = g.CreateNode(LtxNodeTypes.LTXICLoRALoaderModelOnly, new JObject()
        {
            ["model"] = genInfo.Model.Path,
            ["lora_name"] = lora.ToString(g.ModelFolderFormat),
            ["strength_model"] = 1
        });
        genInfo.Model = genInfo.Model.WithPath([loraLoader, 0]);
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
            string scaleNode = g.CreateNode(NodeTypes.ImageScale, new JObject()
            {
                ["image"] = source.Path,
                ["width"] = targetWidth,
                ["height"] = targetHeight,
                ["upscale_method"] = stage.UpscaleMethod["pixel-".Length..],
                ["crop"] = "disabled"
            });
            g.CurrentMedia = source.WithPath([scaleNode, 0]);
            g.CurrentMedia.Width = targetWidth;
            g.CurrentMedia.Height = targetHeight;
            return g.CurrentMedia;
        }

        if (stage.UpscaleMethod.StartsWith("model-", StringComparison.OrdinalIgnoreCase))
        {
            string loaderNode = g.CreateNode(NodeTypes.UpscaleModelLoader, new JObject()
            {
                ["model_name"] = stage.UpscaleMethod["model-".Length..]
            });
            string modelUpscaleNode = g.CreateNode(NodeTypes.ImageUpscaleWithModel, new JObject()
            {
                ["upscale_model"] = new JArray(loaderNode, 0),
                ["image"] = source.Path
            });
            string fitScaleNode = g.CreateNode(NodeTypes.ImageScale, new JObject()
            {
                ["image"] = new JArray(modelUpscaleNode, 0),
                ["width"] = targetWidth,
                ["height"] = targetHeight,
                ["upscale_method"] = "lanczos",
                ["crop"] = "disabled"
            });
            g.CurrentMedia = source.WithPath([fitScaleNode, 0]);
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
        if (priorOutputPath is null
            || newOutputPath is null
            || priorOutputPath.Count != 2
            || newOutputPath.Count != 2
            || JToken.DeepEquals(priorOutputPath, newOutputPath))
        {
            return;
        }

        JArray newAudioPath = retargetAudio ? CopyPath(g.CurrentMedia?.AttachedAudio?.Path) : null;
        Dictionary<string, JArray> staleAudioPaths = [];
        foreach (WorkflowInputConnection connection in WorkflowUtils.FindInputConnections(g.Workflow, priorOutputPath))
        {
            if (!StringUtils.Equals(connection.InputName, "images"))
            {
                continue;
            }
            if (g.Workflow[connection.NodeId] is not JObject node
                || !StringUtils.NodeTypeMatches(node, NodeTypes.SwarmSaveAnimationWS)
                || node["inputs"] is not JObject inputs)
            {
                continue;
            }

            connection.Connection[0] = newOutputPath[0];
            connection.Connection[1] = newOutputPath[1];
            if (!retargetAudio)
            {
                continue;
            }
            if (CopyPath(inputs["audio"] as JArray) is JArray oldAudioPath)
            {
                staleAudioPaths.TryAdd($"{oldAudioPath[0]}:{oldAudioPath[1]}", oldAudioPath);
            }
            if (newAudioPath is not null)
            {
                inputs["audio"] = new JArray(newAudioPath[0], newAudioPath[1]);
            }
            else
            {
                inputs.Remove("audio");
            }
        }

        HashSet<string> protectedNodes = [];
        AddCurrentMediaRootNodeId(protectedNodes, g.CurrentMedia);
        if (newAudioPath is not null)
        {
            protectedNodes.Add($"{newAudioPath[0]}");
        }
        foreach (JArray staleAudioPath in staleAudioPaths.Values)
        {
            WorkflowUtils.RemoveUnusedUpstreamNodes(g.Workflow, staleAudioPath, protectedNodes);
        }
    }

    internal static JArray CopyPath(JArray path)
    {
        if (path is null || path.Count != 2)
        {
            return null;
        }
        return new JArray(path[0], path[1]);
    }

    private static void AddCurrentMediaRootNodeId(HashSet<string> protectedNodes, WGNodeData media)
    {
        if (media?.Path is not JArray currentPath || currentPath.Count != 2)
        {
            return;
        }
        protectedNodes.Add($"{currentPath[0]}");
    }

    private void CleanupReplacedTextToVideoRootStage(JArray priorOutputPath, bool replaceTextToVideoRootStage)
    {
        if (!replaceTextToVideoRootStage || priorOutputPath is null || priorOutputPath.Count != 2)
        {
            return;
        }

        HashSet<string> protectedNodes = [];
        AddCurrentMediaRootNodeId(protectedNodes, g.CurrentMedia);
        WorkflowUtils.RemoveUnusedUpstreamNodes(g.Workflow, priorOutputPath, protectedNodes);
    }

    private static WGNodeData CloneMedia(WGNodeData media)
    {
        if (media?.Path is not JArray path || path.Count != 2)
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
