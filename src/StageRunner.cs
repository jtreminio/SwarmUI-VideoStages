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

internal sealed record StageGenerationPlan(
    WorkflowGenerator.ImageToVideoGenInfo GenInfo,
    Action<WorkflowGenerator.ImageToVideoGenInfo> ApplySourceVideoLatent);

internal class StageRunner(
    WorkflowGenerator g,
    StageGuideMediaHelper stageGuideMediaHelper,
    LtxManager ltxManager,
    Base2EditPublishedStageRefs base2EditPublishedStageRefs)
{
    public void RunStage(
        StageSpec stage,
        int sectionId,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        ClipContext clipContext)
    {
        if (g.CurrentMedia is null)
        {
            Logs.Error($"VideoStages: Stage {stage.Id} has no input media available.");
            return;
        }

        ClipSpec clip = clipContext.Clip;
        using ParamSnapshot loraScope = PromptParser.ApplyLoraScope(
            g.UserInput,
            clip.Id,
            sectionId);

        StageFrame stageFrame = PrepareStage(stage, sectionId, clipContext);
        if (stageFrame is null)
        {
            return;
        }

        WorkflowGenerator.ImageToVideoGenInfo genInfo = stageFrame.Plan.GenInfo;
        using IDisposable controlNetScope = AltImageToVideoScope.Post(genInfo, currentGenInfo =>
        {
            ApplyControlNetLora(clipContext, currentGenInfo);
            bool needsCrop = new ControlNetApplicator(g).Apply(
                currentGenInfo,
                clip.ControlNetSource,
                stage.ControlNetStrength,
                clipContext.Clip.Frames,
                clip.ClipLengthFromControlNet);
            if (needsCrop)
            {
                stageFrame.NeedsCropGuidesAfterSampler = true;
            }
        });

        if (ltxManager.TryRunLocalStage(
                guideReference,
                refStore,
                genInfo,
                stageFrame,
                stageFrame.Plan.ApplySourceVideoLatent,
                stageFrame.SourceMedia,
                stageFrame.PriorOutputPath,
                stageFrame.PostVideoChain))
        {
            RetargetExistingAnimationSaves(
                stageFrame.PriorOutputPath,
                g.CurrentMedia?.Path,
                retargetAudio: g.CurrentMedia?.AttachedAudio is not null);
        }
        else
        {
            RunNativeStagePath(stageFrame, guideReference, refStore, clipContext);
        }
        CleanupReplacedTextToVideoRootStage(stageFrame.PriorOutputPath, stageFrame.ReplacesTextToVideoRoot);
    }

    private StageFrame PrepareStage(StageSpec stage, int sectionId, ClipContext clipContext)
    {
        JArray priorOutputPath = CopyPath(g.CurrentMedia.Path);
        ltxManager.PrepareReusableAudio(clipContext, stage);
        bool replaceTextToVideoRootStage = clipContext.IsFirstStage(stage) && g.GetVideoStagesSpec().IsTextToVideo;
        LtxPostVideoChainCapture postVideoChain = replaceTextToVideoRootStage
            ? null
            : ltxManager.TryCapturePostVideoChain(clipContext, stage);
        WGNodeData sourceMedia = replaceTextToVideoRootStage
            ? CloneMedia(g.CurrentMedia)
            : ApplyStageUpscaleIfNeeded(clipContext, stage, sectionId);
        if (sourceMedia is null)
        {
            Logs.Error($"VideoStages: Stage {stage.Id} could not resolve source media.");
            return null;
        }
        StageGenerationPlan plan = BuildGenInfo(clipContext, stage, sectionId, sourceMedia);
        if (plan is null)
        {
            return null;
        }
        bool parallelMultiClip =
            g.NodeHelpers.TryGetValue(MultiClipParallelMerger.NodeHelperKey, out string parallelFlag)
            && StringUtils.Equals(parallelFlag, "1");
        return new StageFrame(
            stage,
            sectionId,
            clipContext,
            priorOutputPath,
            replaceTextToVideoRootStage,
            postVideoChain,
            sourceMedia,
            plan,
            parallelMultiClip);
    }

    private void RunNativeStagePath(
        StageFrame stageFrame,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        ClipContext clipContext)
    {
        StageSpec stage = stageFrame.Stage;
        WGNodeData guideRaw = stageGuideMediaHelper.ResolveGuideMedia(guideReference, stageFrame.PostVideoChain);
        WanStageReferenceHandler.WanGuideResolution wanRefs = WanStageReferenceHandler.TryResolveClipRefs(
            g,
            stageGuideMediaHelper,
            base2EditPublishedStageRefs,
            clipContext.Clip,
            stage,
            refStore,
            stageFrame.PostVideoChain);
        if (wanRefs.StartRaw is not null)
        {
            guideRaw = wanRefs.StartRaw;
        }

        WGNodeData guideMedia = stageGuideMediaHelper.PrepareGuideMedia(
            guideRaw,
            stageFrame.SourceMedia,
            scaleToSourceSize: true);
        WGNodeData wanEndPrepared = wanRefs.EndRaw is not null
            ? stageGuideMediaHelper.PrepareGuideMedia(wanRefs.EndRaw, stageFrame.SourceMedia, scaleToSourceSize: false)
            : null;

        RunNativeStage(stage, stageFrame.Plan, stageFrame.SourceMedia, guideMedia, stageFrame.PriorOutputPath, wanEndPrepared, clipContext);
    }

    private void RunNativeStage(
        StageSpec stage,
        StageGenerationPlan generationPlan,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        JArray priorOutputPath,
        WGNodeData wanEndPrepared,
        ClipContext clipContext)
    {
        WorkflowGenerator.ImageToVideoGenInfo genInfo = generationPlan.GenInfo;
        g.CurrentMedia = guideMedia ?? sourceMedia;

        using IDisposable sourceLatentScope = generationPlan.ApplySourceVideoLatent is not null
            ? AltImageToVideoScope.Post(genInfo, generationPlan.ApplySourceVideoLatent)
            : null;
        using IDisposable wanPreScope = AltImageToVideoScope.Pre(genInfo, currentGenInfo =>
            ApplyWanStillImageMediaLenBypass(currentGenInfo, stage, sourceMedia, clipContext));
        using IDisposable wanPostScope = AltImageToVideoScope.Post(genInfo, currentGenInfo =>
        {
            CollapseWanStartImageScaleChain(currentGenInfo);
            WanFirstLastFrameRewriter.TryRewriteToFirstLast(g, clipContext.Clip.ImageRefs, stage, currentGenInfo, wanEndPrepared);
            ApplyConditioningHandoff(stage, currentGenInfo, clipContext);
        });

        g.CreateImageToVideo(genInfo);
        ApplyContinuationEndStep(stage);

        g.CurrentVae = genInfo.Vae;
        StampCurrentMediaMetadata(sourceMedia, genInfo);
        RetargetExistingAnimationSaves(priorOutputPath, g.CurrentMedia?.Path);
    }

    private void ApplyConditioningHandoff(
        StageSpec stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        ClipContext clipContext)
    {
        if (!ShouldReuseConditioningHandoff(stage, genInfo))
        {
            return;
        }

        int clipId = clipContext.Clip.Id;
        if (clipContext.IsFirstStage(stage))
        {
            clipContext.LastConditioningHandoff = new ConditioningHandoff(
                clipId,
                CopyPath(genInfo.PosCond),
                CopyPath(genInfo.NegCond));
            return;
        }

        ConditioningHandoff handoff = clipContext.LastConditioningHandoff;
        if (handoff is null
            || handoff.ClipId != clipId
            || handoff.Positive is null
            || handoff.Negative is null)
        {
            return;
        }

        JArray stalePositive = CopyPath(genInfo.PosCond);
        JArray staleNegative = CopyPath(genInfo.NegCond);
        genInfo.PosCond = CopyPath(handoff.Positive);
        genInfo.NegCond = CopyPath(handoff.Negative);
        RemoveUnusedConditioningHandoff(stalePositive, staleNegative);
    }

    private static bool ShouldReuseConditioningHandoff(
        StageSpec stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        return VideoStageModelCompat.SupportsWanFirstLastFrame(genInfo.VideoModel)
            && genInfo.PosCond is { Count: 2 }
            && genInfo.NegCond is { Count: 2 };
    }

    private void ApplyContinuationEndStep(StageSpec stage)
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
        SwarmKSamplerNode samplerNode = startNode is SwarmKSamplerNode start
            ? start
            : bridge.Graph.FindNearestUpstream<SwarmKSamplerNode>(startNode);
        if (samplerNode is null)
        {
            return;
        }

        int steps = Math.Max(1, samplerNode.Steps.LiteralAsInt() ?? stage.Steps);
        int endStep = Math.Clamp(stage.EndStep.Value, 0, steps);
        samplerNode.With(
            EndAtStep: endStep,
            ReturnWithLeftoverNoise: endStep < steps ? "enable" : "disable");
        bridge.SyncNode(samplerNode);
    }

    private void RemoveUnusedConditioningHandoff(JArray stalePositive, JArray staleNegative)
    {
        if (stalePositive is null)
        {
            return;
        }

        HashSet<string> protectedNodes = [];
        AddCurrentMediaRootNodeId(protectedNodes, g.CurrentMedia);
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        WorkflowGraphCleanup.RemoveUnusedUpstreamNodes(bridge, $"{stalePositive[0]}", protectedNodes);
        if (staleNegative is not null && !JToken.DeepEquals(staleNegative, stalePositive))
        {
            WorkflowGraphCleanup.RemoveUnusedUpstreamNodes(bridge, $"{staleNegative[0]}", protectedNodes);
        }
    }

    private StageGenerationPlan BuildGenInfo(
        ClipContext clipContext,
        StageSpec stage,
        int sectionId,
        WGNodeData sourceMedia)
    {
        ClipSpec clip = clipContext.Clip;
        ClipDimensionState dimensions = clipContext.Dimensions;
        VideoStagesSpec spec = g.GetVideoStagesSpec();
        T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null, sectionId: sectionId);
        if (videoModel is null)
        {
            Logs.Error($"VideoStages: Stage {stage.Id} could not resolve video model '{stage.Model}'.");
            return null;
        }
        _ = g.NodeHelpers.Remove($"modelloader_{videoModel.Name}_image2video");

        bool sourceIsVideo = sourceMedia.DataType == WGNodeData.DT_VIDEO;
        bool shouldUseLocalLtxv2Path = VideoStageModelCompat.IsLtxV2VideoModel(videoModel) && sourceIsVideo;
        bool isWanStage = VideoStageModelCompat.IsWanVideoModel(videoModel);
        (int batchIndex, int batchLen) = sourceIsVideo ? (0, 1) : (-1, -1);

        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent = sourceIsVideo && !shouldUseLocalLtxv2Path
            ? BuildSourceVideoLatentApplier(stage, sourceMedia, isWanStage)
            : null;

        (string positivePrompt, string negativePrompt) = BuildStagePrompts(clip, stage);

        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = g,
            VideoModel = videoModel,
            VideoSwapModel = null,
            VideoSwapPercent = 0.5,
            Frames = ResolveFrames(sourceMedia, sectionId),
            VideoCFG = stage.CfgScale,
            VideoFPS = spec.FPS,
            Width = sourceMedia.Width ?? dimensions.Width,
            Height = sourceMedia.Height ?? dimensions.Height,
            Prompt = positivePrompt,
            NegativePrompt = negativePrompt,
            Steps = stage.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.Id,
            BatchIndex = batchIndex,
            BatchLen = batchLen,
            ContextID = sectionId,
            VideoEndFrame = g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
        };
        return new StageGenerationPlan(genInfo, applySourceVideoLatent);
    }

    private int? ResolveFrames(WGNodeData sourceMedia, int sectionId)
    {
        if (sourceMedia.Frames.HasValue)
        {
            return sourceMedia.Frames;
        }
        if (g.UserInput.TryGet(T2IParamTypes.VideoFrames, out int explicitFrames, sectionId: sectionId))
        {
            return explicitFrames;
        }
        if (g.UserInput.TryGet(T2IParamTypes.Text2VideoFrames, out int textToVideoFrames, sectionId: sectionId))
        {
            return textToVideoFrames;
        }
        return null;
    }

private Action<WorkflowGenerator.ImageToVideoGenInfo> BuildSourceVideoLatentApplier(
        StageSpec stage,
        WGNodeData sourceMedia,
        bool isWanStage)
    {
        return genInfo =>
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
            ImageFromBatchNode fromBatch = AddImageFromBatch(sourceMedia.Path, batchIndex: 0, length: genInfo.Frames.Value);
            genInfo.StartStep = (int)Math.Floor(stage.Steps * (1 - stage.Control));
            g.CurrentMedia = sourceMedia.WithPath([fromBatch.Id, 0]);
            g.CurrentMedia.Frames = Math.Min(genInfo.Frames.Value, g.CurrentMedia.Frames ?? int.MaxValue);
            g.CurrentMedia = g.CurrentMedia.AsLatentImage(genInfo.Vae);
        };
    }

    private (string Positive, string Negative) BuildStagePrompts(ClipSpec clip, StageSpec stage)
    {
        string positive = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negative = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        string originalPositive = PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.Prompt.Type.ID, positive);
        string originalNegative = PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.NegativePrompt.Type.ID, negative);
        return (
            PromptParser.ExtractPrompt(positive, originalPositive, clip.Id, stage.Id, stage.ClipStageIndex),
            PromptParser.ExtractPrompt(negative, originalNegative, clip.Id, stage.Id, stage.ClipStageIndex));
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

    private ImageFromBatchNode AddImageFromBatch(JArray imagePath, int batchIndex, int length)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode()).With(
            BatchIndex: batchIndex,
            Length: length);
        node.Image.ConnectFromPath(bridge, imagePath);
        bridge.SyncNode(node);
        BridgeSync.SyncLastId(g);
        return node;
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

        upstreamScale.With(
            Width: startScale.Width.LiteralAsLong() ?? 0L,
            Height: startScale.Height.LiteralAsLong() ?? 0L,
            Crop: startScale.Crop.LiteralAsString() ?? "center",
            UpscaleMethod: startScale.UpscaleMethod.LiteralAsString() ?? "lanczos");
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
        StageSpec stage,
        WGNodeData sourceMedia,
        ClipContext clipContext)
    {
        WorkflowGenerator workflowGenerator = genInfo.Generator;
        if (!VideoStageModelCompat.SupportsWanFirstLastFrame(genInfo.VideoModel)
            || !clipContext.IsFirstStage(stage)
            || sourceMedia.DataType == WGNodeData.DT_VIDEO
            || workflowGenerator.CurrentMedia is null
            || workflowGenerator.CurrentMedia.DataType == WGNodeData.DT_VIDEO)
        {
            return;
        }

        genInfo.HasFixedMediaLen = true;
    }

    private void ApplyControlNetLora(ClipContext clipContext, WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        ClipSpec clip = clipContext.Clip;
        if (string.IsNullOrWhiteSpace(clip.ControlNetLora) || genInfo.Model is null)
        {
            return;
        }

        if (!VideoStageModelCompat.IsLtxV2VideoModel(genInfo.VideoModel))
        {
            return;
        }

        if (clipContext.CachedControlNetLoraModelPath is JArray cached && cached.Count == 2)
        {
            genInfo.Model = genInfo.Model.WithPath(CopyPath(cached));
            return;
        }

        T2IModel lora = ResolveLoraModel(clip.ControlNetLora);
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
        LTXICLoRALoaderModelOnlyNode loraLoader = bridge.AddNode(new LTXICLoRALoaderModelOnlyNode()).With(
            LoraName: lora.ToString(g.ModelFolderFormat),
            StrengthModel: 1.0);
        if (genInfo.Model?.Path is JArray modelPath)
        {
            loraLoader.ModelInput.ConnectFromPath(bridge, modelPath);
        }
        bridge.SyncNode(loraLoader);
        BridgeSync.SyncLastId(g);
        genInfo.Model = genInfo.Model.WithPath([loraLoader.Id, 0]);
        clipContext.CachedControlNetLoraModelPath = new JArray(loraLoader.Id, 0);
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

    private WGNodeData ApplyStageUpscaleIfNeeded(ClipContext clipContext, StageSpec stage, int sectionId)
    {
        ClipDimensionState dimensions = clipContext.Dimensions;
        WGNodeData source = VaeDecodePreference.AsRawImage(g, g.CurrentMedia, g.CurrentVae);
        int width = Math.Max(source.Width ?? dimensions.Width, 16);
        int height = Math.Max(source.Height ?? dimensions.Height, 16);
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
            dimensions.Width = targetWidth;
            dimensions.Height = targetHeight;
            return g.CurrentMedia;
        }

        if (stage.UpscaleMethod.StartsWith("model-", StringComparison.OrdinalIgnoreCase))
        {
            string modelName = stage.UpscaleMethod["model-".Length..];
            ImageScaleNode fitScale = AddModelUpscaleChain(source.Path, modelName, targetWidth, targetHeight);
            g.CurrentMedia = source.WithPath([fitScale.Id, 0]);
            g.CurrentMedia.Width = targetWidth;
            g.CurrentMedia.Height = targetHeight;
            dimensions.Width = targetWidth;
            dimensions.Height = targetHeight;
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
        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode().With(
            Width: width,
            Height: height,
            UpscaleMethod: upscaleMethod,
            Crop: "disabled"));
        scale.Image.ConnectFromPath(bridge, sourcePath);
        bridge.SyncNode(scale);
        BridgeSync.SyncLastId(g);
        return scale;
    }

    private ImageScaleNode AddModelUpscaleChain(JArray sourcePath, string modelName, int targetWidth, int targetHeight)
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        UpscaleModelLoaderNode loader = bridge.AddNode(new UpscaleModelLoaderNode()).With(
            ModelName: modelName);
        bridge.SyncNode(loader);

        ImageUpscaleWithModelNode upscale = bridge.AddNode(new ImageUpscaleWithModelNode());
        upscale.UpscaleModel.ConnectTo(loader.UPSCALEMODEL);
        upscale.Image.ConnectFromPath(bridge, sourcePath);
        bridge.SyncNode(upscale);

        ImageScaleNode fit = bridge.AddNode(new ImageScaleNode().With(
            Width: targetWidth,
            Height: targetHeight,
            UpscaleMethod: "lanczos",
            Crop: "disabled"));
        fit.Image.ConnectTo(upscale.IMAGE);
        bridge.SyncNode(fit);
        BridgeSync.SyncLastId(g);
        return fit;
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
        HashSet<string> staleAudioNodeIds = [];

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
                    staleAudioNodeIds.Add(oldAudioOutput.Node.Id);
                }
                if (!saveNode.Audio.TryConnectToUntyped(newAudioOutput))
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
        foreach (string staleAudioNodeId in staleAudioNodeIds)
        {
            WorkflowGraphCleanup.RemoveUnusedUpstreamNodes(bridge, staleAudioNodeId, protectedNodes);
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

    private void CleanupReplacedTextToVideoRootStage(JArray priorOutputPath, bool replaceTextToVideoRootStage)
    {
        if (!replaceTextToVideoRootStage || priorOutputPath is not { Count: 2 })
        {
            return;
        }

        HashSet<string> protectedNodes = [];
        AddCurrentMediaRootNodeId(protectedNodes, g.CurrentMedia);
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        WorkflowGraphCleanup.RemoveUnusedUpstreamNodes(bridge, $"{priorOutputPath[0]}", protectedNodes);
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
