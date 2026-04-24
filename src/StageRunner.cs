using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.LTX2;

namespace VideoStages;

/// <summary>Runs one VideoStages image-to-video stage (native or local LTX-V2 path).</summary>
internal class StageRunner(WorkflowGenerator g)
{
    private readonly LtxStageOrchestrator _ltxStageOrchestrator = new(g);

    private sealed record StageGenerationPlan(
        WorkflowGenerator.ImageToVideoGenInfo GenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> ApplySourceVideoLatent);

    public void RunStage(
        JsonParser.StageSpec stage,
        int sectionId,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore)
    {
        if (g.CurrentMedia is null)
        {
            throw new SwarmReadableErrorException($"VideoStages: Stage {stage.Id} has no input media available.");
        }

        JArray priorOutputPath = CopyPath(g.CurrentMedia.Path);
        LtxAudioReuseState.PrepareReusableAudio(g, stage);
        bool replaceTextToVideoRootStage = RootVideoStageTakeover.ShouldReplaceTextToVideoRootStage(g, stage);
        LtxPostVideoChain postVideoChain = replaceTextToVideoRootStage ? null : LtxPostVideoChain.TryCapture(g, stage);
        WGNodeData sourceMedia = replaceTextToVideoRootStage ? CloneMedia(g.CurrentMedia) : ApplyStageUpscaleIfNeeded(stage, sectionId);
        StageGenerationPlan generationPlan = BuildGenInfo(stage, sectionId, sourceMedia);
        WorkflowGenerator.ImageToVideoGenInfo genInfo = generationPlan.GenInfo;

        if (_ltxStageOrchestrator.TryRunLocalLtxPath(
                stage,
                guideReference,
                refStore,
                genInfo,
                generationPlan.ApplySourceVideoLatent,
                sourceMedia,
                priorOutputPath,
                postVideoChain))
        {
            RetargetExistingAnimationSaves(priorOutputPath, g.CurrentMedia?.Path, retargetAudio: g.CurrentMedia?.AttachedAudio is not null);
            CleanupReplacedTextToVideoRootStage(priorOutputPath, replaceTextToVideoRootStage);
            return;
        }

        WGNodeData guideMedia = StageGuideMediaHelper.PrepareGuideMedia(
            g,
            StageGuideMediaHelper.ResolveGuideMedia(g, guideReference, postVideoChain),
            sourceMedia,
            scaleToSourceSize: true);

        RunNativeStage(generationPlan, sourceMedia, guideMedia, priorOutputPath);
        CleanupReplacedTextToVideoRootStage(priorOutputPath, replaceTextToVideoRootStage);
    }

    private void RunNativeStage(
        StageGenerationPlan generationPlan,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        JArray priorOutputPath)
    {
        WorkflowGenerator.ImageToVideoGenInfo genInfo = generationPlan.GenInfo;
        g.CurrentMedia = guideMedia ?? sourceMedia;
        Action<WorkflowGenerator.ImageToVideoGenInfo> postHandler = null;
        if (generationPlan.ApplySourceVideoLatent is not null)
        {
            postHandler = currentGenInfo =>
            {
                if (!ReferenceEquals(currentGenInfo, genInfo))
                {
                    return;
                }
                generationPlan.ApplySourceVideoLatent(currentGenInfo);
            };
            WorkflowGenerator.AltImageToVideoPostHandlers.Add(postHandler);
        }
        try
        {
            g.CreateImageToVideo(genInfo);
        }
        finally
        {
            if (postHandler is not null)
            {
                _ = WorkflowGenerator.AltImageToVideoPostHandlers.Remove(postHandler);
            }
        }
        g.CurrentVae = genInfo.Vae;
        StampCurrentMediaMetadata(sourceMedia, genInfo);
        RetargetExistingAnimationSaves(priorOutputPath, g.CurrentMedia?.Path);
    }

    private StageGenerationPlan BuildGenInfo(
        JsonParser.StageSpec stage,
        int sectionId,
        WGNodeData sourceMedia)
    {
        T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null, sectionId: sectionId);
        if (videoModel is null)
        {
            throw new SwarmReadableErrorException($"VideoStages: Stage {stage.Id} could not resolve video model '{stage.Model}'.");
        }

        bool shouldUseLocalLtxv2Path = videoModel.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatLtxv2.ID
            && sourceMedia.DataType == WGNodeData.DT_VIDEO;
        int? frames = sourceMedia.Frames;
        if (!frames.HasValue && g.UserInput.TryGet(T2IParamTypes.VideoFrames, out int explicitFrames, sectionId: sectionId))
        {
            frames = explicitFrames;
        }
        if (!frames.HasValue && g.UserInput.TryGet(T2IParamTypes.Text2VideoFrames, out int textToVideoFrames, sectionId: sectionId))
        {
            frames = textToVideoFrames;
        }

        int? fps = sourceMedia.FPS;
        if (!fps.HasValue && g.UserInput.TryGet(T2IParamTypes.VideoFPS, out int explicitFps, sectionId: sectionId))
        {
            fps = explicitFps;
        }

        bool sourceIsVideo = sourceMedia.DataType == WGNodeData.DT_VIDEO;
        Action<WorkflowGenerator.ImageToVideoGenInfo> applySourceVideoLatent = null;
        int batchIndex = -1;
        int batchLen = -1;
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
                    string fromBatch = g.CreateNode("ImageFromBatch", new JObject()
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
            Prompt = g.UserInput.Get(T2IParamTypes.Prompt, ""),
            NegativePrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, ""),
            Steps = stage.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.Id,
            BatchIndex = batchIndex,
            BatchLen = batchLen,
            ContextID = sectionId,
            VideoEndFrame = g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
        };
        return new StageGenerationPlan(genInfo, applySourceVideoLatent);
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
        bool isLtxv2Stage = stageVideoModel?.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatLtxv2.ID;
        if (isLtxv2Stage)
        {
            if (IsSupportedLtxUpscaleMethod(stage.UpscaleMethod))
            {
                g.CurrentMedia = source;
                return source;
            }

            Logs.Warning($"VideoStages: Stage {stage.Id} uses unsupported LTX upscale method '{stage.UpscaleMethod}'. Ignoring upscale.");
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
            Logs.Warning($"VideoStages: Stage {stage.Id} uses unsupported upscale method '{stage.UpscaleMethod}'. Ignoring upscale.");
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

    private void RetargetExistingAnimationSaves(JArray priorOutputPath, JArray newOutputPath, bool retargetAudio = false)
    {
        if (priorOutputPath is null
            || newOutputPath is null
            || priorOutputPath.Count != 2
            || newOutputPath.Count != 2
            || JToken.DeepEquals(priorOutputPath, newOutputPath))
        {
            return;
        }

        JArray newAudioPath = retargetAudio ? g.CurrentMedia?.AttachedAudio?.Path : null;
        List<JArray> staleAudioPaths = [];
        foreach (WorkflowInputConnection connection in WorkflowUtils.FindInputConnections(g.Workflow, priorOutputPath))
        {
            if (!string.Equals(connection.InputName, "images", StringComparison.Ordinal))
            {
                continue;
            }
            if (g.Workflow[connection.NodeId] is not JObject node
                || $"{node["class_type"]}" != NodeTypes.SwarmSaveAnimationWS
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
            if (inputs["audio"] is JArray oldAudioPath && oldAudioPath.Count == 2)
            {
                staleAudioPaths.Add(new JArray(oldAudioPath[0], oldAudioPath[1]));
            }
            if (newAudioPath is not null && newAudioPath.Count == 2)
            {
                inputs["audio"] = new JArray(newAudioPath[0], newAudioPath[1]);
            }
            else
            {
                inputs.Remove("audio");
            }
        }

        HashSet<string> protectedNodes = [];
        if (g.CurrentMedia?.Path is JArray currentPath && currentPath.Count == 2)
        {
            protectedNodes.Add($"{currentPath[0]}");
        }
        if (newAudioPath is not null && newAudioPath.Count == 2)
        {
            protectedNodes.Add($"{newAudioPath[0]}");
        }
        foreach (JArray staleAudioPath in staleAudioPaths)
        {
            WorkflowUtils.RemoveUnusedUpstreamNodes(g.Workflow, staleAudioPath, protectedNodes);
        }
    }

    private static JArray CopyPath(JArray path)
    {
        if (path is null || path.Count != 2)
        {
            return null;
        }
        return new JArray(path[0], path[1]);
    }

    private void CleanupReplacedTextToVideoRootStage(JArray priorOutputPath, bool replaceTextToVideoRootStage)
    {
        if (!replaceTextToVideoRootStage || priorOutputPath is null || priorOutputPath.Count != 2)
        {
            return;
        }

        HashSet<string> protectedNodes = [];
        if (g.CurrentMedia?.Path is JArray currentPath && currentPath.Count == 2)
        {
            protectedNodes.Add($"{currentPath[0]}");
        }
        WorkflowUtils.RemoveUnusedUpstreamNodes(g.Workflow, priorOutputPath, protectedNodes);
    }

    private static WGNodeData CloneMedia(WGNodeData media)
    {
        if (media?.Path is not JArray path || path.Count != 2)
        {
            return null;
        }
        WGNodeData clone = media.WithPath(new JArray(path[0], path[1]), media.DataType, media.Compat);
        clone.Width = media.Width;
        clone.Height = media.Height;
        clone.Frames = media.Frames;
        clone.FPS = media.FPS;
        if (media.AttachedAudio?.Path is JArray audioPath && audioPath.Count == 2)
        {
            clone.AttachedAudio = media.AttachedAudio.WithPath(
                new JArray(audioPath[0], audioPath[1]),
                media.AttachedAudio.DataType,
                media.AttachedAudio.Compat);
        }
        return clone;
    }
}
