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

namespace VideoStages;

internal sealed record StageGenerationPlan(
    WorkflowGenerator.ImageToVideoGenInfo GenInfo,
    Action<WorkflowGenerator.ImageToVideoGenInfo> ApplySourceVideoLatent);

internal class StageRunner(
    WorkflowGenerator g,
    StageGuideMediaHelper stageGuideMediaHelper,
    LtxManager ltxManager)
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
        using ParamSnapshot promptLoraScope = PromptParser.ApplyLoraScope(g.UserInput, clip.Id, sectionId);
        using ParamSnapshot loraScope = ApplyStageLoras(g.UserInput, clip, stage);

        StageFrame stageFrame = PrepareStage(stage, sectionId, clipContext);
        if (stageFrame is null)
        {
            return;
        }

        WorkflowGenerator.ImageToVideoGenInfo genInfo = stageFrame.Plan.GenInfo;
        using IDisposable controlNetScope = AltImageToVideoScope.Post(genInfo, currentGenInfo =>
        {
            bool needsCrop = new ControlNetApplicator(g).ApplyIcLoras(
                currentGenInfo,
                clip,
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
            RunNativeStagePath(stageFrame, guideReference);
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
            : ApplyStageUpscaleIfNeeded(clipContext, stage, sectionId, postVideoChain);
        if (sourceMedia is null)
        {
            Logs.Error($"VideoStages: Stage {stage.Id} could not resolve source media.");
            return null;
        }
        StageGenerationPlan plan = BuildGenInfo(clipContext, stage, sectionId, sourceMedia, replaceTextToVideoRootStage);
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
        StageRefStore.StageRef guideReference)
    {
        StageSpec stage = stageFrame.Stage;
        WGNodeData guideRaw = stageGuideMediaHelper.ResolveGuideMedia(guideReference, stageFrame.PostVideoChain);

        WGNodeData guideMedia = stageGuideMediaHelper.PrepareGuideMedia(
            guideRaw,
            stageFrame.SourceMedia,
            scaleToSourceSize: true);

        RunNativeStage(stage, stageFrame.Plan, stageFrame.SourceMedia, guideMedia, stageFrame.PriorOutputPath);
    }

    private void RunNativeStage(
        StageSpec stage,
        StageGenerationPlan generationPlan,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        JArray priorOutputPath)
    {
        WorkflowGenerator.ImageToVideoGenInfo genInfo = generationPlan.GenInfo;
        g.CurrentMedia = guideMedia ?? sourceMedia;

        using IDisposable sourceLatentScope = generationPlan.ApplySourceVideoLatent is not null
            ? AltImageToVideoScope.Post(genInfo, generationPlan.ApplySourceVideoLatent)
            : null;

        g.CreateImageToVideo(genInfo);

        g.CurrentVae = genInfo.Vae;
        StampCurrentMediaMetadata(sourceMedia, genInfo);
        RetargetExistingAnimationSaves(priorOutputPath, g.CurrentMedia?.Path);
    }

    private StageGenerationPlan BuildGenInfo(
        ClipContext clipContext,
        StageSpec stage,
        int sectionId,
        WGNodeData sourceMedia,
        bool replaceTextToVideoRootStage)
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
        (int batchIndex, int batchLen) = sourceIsVideo ? (0, 1) : (-1, -1);

        (string positivePrompt, string negativePrompt) = BuildClipPrompts(clip, stage);

        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = g,
            VideoModel = videoModel,
            VideoSwapModel = null,
            VideoSwapPercent = 0.5,
            Frames = ResolveFrames(sourceMedia, sectionId, replaceTextToVideoRootStage),
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
        return new StageGenerationPlan(genInfo, ApplySourceVideoLatent: null);
    }

    private int? ResolveFrames(WGNodeData sourceMedia, int sectionId, bool replaceTextToVideoRootStage = false)
    {
        if (!replaceTextToVideoRootStage && sourceMedia.Frames.HasValue)
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
        return sourceMedia.Frames;
    }

    private (string Positive, string Negative) BuildClipPrompts(ClipSpec clip, StageSpec stage)
    {
        string positive = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negative = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        string originalPositive = PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.Prompt.Type.ID, positive);
        string originalNegative = PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.NegativePrompt.Type.ID, negative);
        return (
            PromptParser.ExtractPrompt(positive, originalPositive, clip.Id, stage.Id, stage.ClipStageIndex),
            PromptParser.ExtractPrompt(negative, originalNegative, clip.Id, stage.Id, stage.ClipStageIndex));
    }

    private static ParamSnapshot ApplyStageLoras(T2IParamInput input, ClipSpec clip, StageSpec stage)
    {
        List<LoraRef> toApply = [];
        if (clip.Loras is not null)
        {
            toApply.AddRange(clip.Loras);
        }
        if (stage.Loras is not null)
        {
            toApply.AddRange(stage.Loras);
        }
        if (toApply.Count == 0)
        {
            return null;
        }

        List<string> loras = [.. input.Get(T2IParamTypes.Loras) ?? []];
        List<string> weights = [.. input.Get(T2IParamTypes.LoraWeights) ?? []];
        List<string> tencWeights = [.. input.Get(T2IParamTypes.LoraTencWeights) ?? []];
        List<string> confinements = [.. input.Get(T2IParamTypes.LoraSectionConfinement) ?? []];

        while (weights.Count < loras.Count) { weights.Add("1"); }
        while (tencWeights.Count < loras.Count) { tencWeights.Add(weights[tencWeights.Count]); }
        while (confinements.Count < loras.Count) { confinements.Add("-1"); }

        foreach (LoraRef lora in toApply)
        {
            loras.Add(lora.Name);
            weights.Add(FormatLoraWeight(lora.Weight));
            tencWeights.Add(FormatLoraWeight(lora.TencWeight ?? lora.Weight));
            confinements.Add($"{T2IParamInput.SectionID_Video}");
        }

        ParamSnapshot snapshot = ParamSnapshot.Of(input,
            T2IParamTypes.Loras.Type,
            T2IParamTypes.LoraWeights.Type,
            T2IParamTypes.LoraTencWeights.Type,
            T2IParamTypes.LoraSectionConfinement.Type);
        input.Set(T2IParamTypes.Loras, loras);
        input.Set(T2IParamTypes.LoraWeights, weights);
        input.Set(T2IParamTypes.LoraTencWeights, tencWeights);
        input.Set(T2IParamTypes.LoraSectionConfinement, confinements);
        return snapshot;
    }

    private static string FormatLoraWeight(double weight) =>
        weight.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private WGNodeData ApplyStageUpscaleIfNeeded(
        ClipContext clipContext,
        StageSpec stage,
        int sectionId,
        LtxPostVideoChainCapture postVideoChain)
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
        if (isLtxv2Stage && (stage.IsLatentModelUpscale || stage.IsLatentUpscale))
        {
            g.CurrentMedia = source;
            return source;
        }

        WGNodeData upscaleSource = ResolveUpscaleSourceMedia(source, postVideoChain, width, height);

        if (stage.IsPixelUpscale)
        {
            string method = stage.UpscaleMethod["pixel-".Length..];
            ImageScaleNode scaleNode = AddDisabledCropImageScale(upscaleSource.Path, targetWidth, targetHeight, method);
            g.CurrentMedia = upscaleSource.WithPath(scaleNode.IMAGE);
            g.CurrentMedia.Width = targetWidth;
            g.CurrentMedia.Height = targetHeight;
            dimensions.Width = targetWidth;
            dimensions.Height = targetHeight;
            return g.CurrentMedia;
        }

        if (stage.IsModelUpscale)
        {
            string modelName = stage.UpscaleMethod["model-".Length..];
            ImageScaleNode fitScale = AddModelUpscaleChain(upscaleSource.Path, modelName, targetWidth, targetHeight);
            g.CurrentMedia = upscaleSource.WithPath(fitScale.IMAGE);
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

    private WGNodeData ResolveUpscaleSourceMedia(
        WGNodeData source,
        LtxPostVideoChainCapture postVideoChain,
        int width,
        int height)
    {
        if (postVideoChain is null || !ReferencesPostVideoChainOutput(source, postVideoChain))
        {
            return source;
        }

        WGNodeData detached = postVideoChain.CreateDetachedGuideMedia(g.CurrentVae);
        if (detached is null)
        {
            return source;
        }
        detached.Width = width;
        detached.Height = height;
        return detached;
    }

    private static bool ReferencesPostVideoChainOutput(WGNodeData media, LtxPostVideoChainCapture postVideoChain)
    {
        return media?.Path is JArray mediaPath
            && (JToken.DeepEquals(mediaPath, postVideoChain.CurrentOutputMedia?.Path)
                || JToken.DeepEquals(mediaPath, postVideoChain.DecodeOutputPath));
    }

    private ImageScaleNode AddDisabledCropImageScale(JArray sourcePath, int width, int height, string upscaleMethod)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode().With(
            Width: width,
            Height: height,
            UpscaleMethod: upscaleMethod,
            Crop: "disabled"));
        scale.Image.ConnectFromPath(bridge, sourcePath);
        return scale;
    }

    private ImageScaleNode AddModelUpscaleChain(JArray sourcePath, string modelName, int targetWidth, int targetHeight)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        UpscaleModelLoaderNode loader = bridge.AddNode(new UpscaleModelLoaderNode()).With(
            ModelName: modelName);

        ImageUpscaleWithModelNode upscale = bridge.AddNode(new ImageUpscaleWithModelNode().With(
            UpscaleModel: loader.UPSCALEMODEL));
        upscale.Image.ConnectFromPath(bridge, sourcePath);

        ImageScaleNode fit = bridge.AddNode(new ImageScaleNode().With(
            Width: targetWidth,
            Height: targetHeight,
            UpscaleMethod: "lanczos",
            Crop: "disabled",
            Image: upscale.IMAGE));
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

    internal void RetargetExistingAnimationSaves(
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

        WGNodeData attachedAudio = g.CurrentMedia?.AttachedAudio;
        if (retargetAudio && attachedAudio?.DataType == WGNodeData.DT_LATENT_AUDIO && g.CurrentAudioVae is not null)
        {
            attachedAudio = attachedAudio.DecodeLatents(g.CurrentAudioVae, true);
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput oldOutput = bridge.ResolvePath(priorOutputPath);
        INodeOutput newOutput = bridge.ResolvePath(newOutputPath);
        if (oldOutput is null || newOutput is null)
        {
            return;
        }

        JArray newAudioPath = retargetAudio && attachedAudio?.DataType == WGNodeData.DT_AUDIO ? CopyPath(attachedAudio.Path) : null;
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
