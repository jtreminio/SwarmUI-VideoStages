using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.LTX2;

namespace VideoStages;

/// <summary>
/// Executes a single VideoStages stage.
/// </summary>
internal class StageRunner(WorkflowGenerator g)
{
    private readonly StageExecutor _stageExecutor = new(g);

    private sealed record StageGenerationPlan(
        WorkflowGenerator.ImageToVideoGenInfo GenInfo,
        Action<WorkflowGenerator.ImageToVideoGenInfo> ApplySourceVideoLatent);

    /// <summary>
    /// Runs one image-to-video stage using the current generator state as input.
    /// </summary>
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
        PostVideoChain postVideoChain = PostVideoChain.TryCapture(g);
        WGNodeData sourceMedia = ApplyStageUpscaleIfNeeded(stage, sectionId);
        StageGenerationPlan generationPlan = BuildGenInfo(stage, sectionId, sourceMedia);
        WorkflowGenerator.ImageToVideoGenInfo genInfo = generationPlan.GenInfo;
        bool useLocalLtxv2Path = ShouldUseLocalLtxv2Path(genInfo, sourceMedia);
        List<ResolvedClipRef> clipRefs = null;
        ResolvedClipRef primaryGuideClipRef = null;
        double guideMergeStrength = 1.0;
        if (useLocalLtxv2Path)
        {
            clipRefs = ResolveStageClipRefs(stage, refStore, postVideoChain, sourceMedia);
            primaryGuideClipRef = ExtractPrimaryGuideClipRef(clipRefs);
            clipRefs = RemovePrimaryGuideClipRef(clipRefs, primaryGuideClipRef);
            if (primaryGuideClipRef is not null)
            {
                guideMergeStrength = primaryGuideClipRef.Strength;
            }
        }
        bool skipGuideReinjection = useLocalLtxv2Path
            && primaryGuideClipRef is null
            && (HasOnlyNonPrimaryClipRefs(clipRefs)
                || ShouldSkipGeneratedGuideReinjection(stage, sourceMedia, guideReference, genInfo, postVideoChain));
        WGNodeData guideMedia = useLocalLtxv2Path
            ? ResolveLocalGuideMedia(primaryGuideClipRef, skipGuideReinjection, sourceMedia, priorOutputPath, postVideoChain)
            : PrepareGuideMedia(
                ResolveGuideMedia(guideReference, postVideoChain),
                sourceMedia,
                scaleToSourceSize: true);

        if (useLocalLtxv2Path)
        {
            _stageExecutor.RunStage(
                stage,
                genInfo,
                sourceMedia,
                guideMedia,
                skipGuideReinjection,
                generationPlan.ApplySourceVideoLatent,
                postVideoChain,
                clipRefs,
                guideMergeStrength);
            return;
        }

        RunNativeStage(generationPlan, sourceMedia, guideMedia, priorOutputPath);
    }

    private List<ResolvedClipRef> ResolveStageClipRefs(
        JsonParser.StageSpec stage,
        StageRefStore refStore,
        PostVideoChain postVideoChain,
        WGNodeData sourceMedia)
    {
        IReadOnlyList<JsonParser.RefSpec> refs = stage.ClipRefs ?? [];
        IReadOnlyList<double> strengths = stage.RefStrengths ?? [];
        List<ResolvedClipRef> resolved = [];
        for (int i = 0; i < refs.Count; i++)
        {
            JsonParser.RefSpec spec = refs[i];
            double strength = i < strengths.Count
                ? strengths[i]
                : VideoStagesExtension.DefaultStageRefStrength;
            WGNodeData raw = ResolveClipRefSourceMedia(spec, refStore, postVideoChain);
            if (raw is null)
            {
                Logs.Warning($"VideoStages: Stage {stage.Id} clip reference {i} ({spec.Source}) could not be resolved; skipping.");
                continue;
            }

            WGNodeData prepared;
            if (PrimaryGuideMatchesScaledSource(raw, sourceMedia))
            {
                prepared = sourceMedia;
            }
            else
            {
                prepared = PrepareGuideMedia(raw, sourceMedia, scaleToSourceSize: false);
            }
            resolved.Add(new ResolvedClipRef(prepared, spec, strength));
        }

        return resolved;
    }

    private static ResolvedClipRef ExtractPrimaryGuideClipRef(IReadOnlyList<ResolvedClipRef> clipRefs)
    {
        if (clipRefs is null)
        {
            return null;
        }

        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (!clipRef.Spec.FromEnd && clipRef.Spec.Frame == 1)
            {
                return clipRef;
            }
        }

        return null;
    }

    private static List<ResolvedClipRef> RemovePrimaryGuideClipRef(
        IReadOnlyList<ResolvedClipRef> clipRefs,
        ResolvedClipRef primaryGuideClipRef)
    {
        if (clipRefs is null || primaryGuideClipRef is null)
        {
            return clipRefs is null ? null : [.. clipRefs];
        }

        List<ResolvedClipRef> remaining = [];
        bool removedPrimary = false;
        foreach (ResolvedClipRef clipRef in clipRefs)
        {
            if (!removedPrimary && ReferenceEquals(clipRef, primaryGuideClipRef))
            {
                removedPrimary = true;
                continue;
            }

            remaining.Add(clipRef);
        }

        return remaining;
    }

    private WGNodeData ResolveLocalGuideMedia(
        ResolvedClipRef primaryGuideClipRef,
        bool skipGuideReinjection,
        WGNodeData sourceMedia,
        JArray priorOutputPath,
        PostVideoChain postVideoChain)
    {
        if (primaryGuideClipRef is null)
        {
            return ResolveDefaultLocalGuideMedia(skipGuideReinjection, sourceMedia, postVideoChain);
        }

        if (primaryGuideClipRef.Image?.Path is JArray guidePath
            && priorOutputPath is not null
            && JToken.DeepEquals(guidePath, priorOutputPath))
        {
            return ResolveDefaultLocalGuideMedia(skipGuideReinjection: false, sourceMedia, postVideoChain);
        }

        if (PrimaryGuideMatchesScaledSource(primaryGuideClipRef.Image, sourceMedia))
        {
            return ResolveDefaultLocalGuideMedia(skipGuideReinjection: false, sourceMedia, postVideoChain);
        }

        return PrepareGuideMedia(primaryGuideClipRef.Image, sourceMedia, scaleToSourceSize: true);
    }

    private bool PrimaryGuideMatchesScaledSource(WGNodeData primaryGuideMedia, WGNodeData sourceMedia)
    {
        if (primaryGuideMedia?.Path is not JArray primaryGuidePath
            || sourceMedia?.Path is not JArray sourcePath
            || sourcePath.Count != 2)
        {
            return false;
        }

        if (g.Workflow[$"{sourcePath[0]}"] is not JObject sourceNode
            || $"{sourceNode["class_type"]}" != NodeTypes.ImageScale
            || sourceNode["inputs"] is not JObject sourceInputs
            || sourceInputs["image"] is not JArray scaledSourceInput)
        {
            return false;
        }

        return JToken.DeepEquals(primaryGuidePath, scaledSourceInput);
    }

    private WGNodeData ResolveDefaultLocalGuideMedia(
        bool skipGuideReinjection,
        WGNodeData sourceMedia,
        PostVideoChain postVideoChain)
    {
        if (skipGuideReinjection)
        {
            return null;
        }

        if (postVideoChain is not null && IsLiveCurrentOutputReference(sourceMedia, postVideoChain))
        {
            WGNodeData detachedGuideVae = postVideoChain.CreateStageInputVae() ?? g.CurrentVae;
            return postVideoChain.CreateDetachedGuideMedia(detachedGuideVae);
        }

        return sourceMedia;
    }

    private WGNodeData ResolveClipRefSourceMedia(
        JsonParser.RefSpec spec,
        StageRefStore refStore,
        PostVideoChain postVideoChain)
    {
        if (string.Equals(spec.Source, "Upload", StringComparison.OrdinalIgnoreCase))
        {
            return MaterializeUploadedRefImage(spec);
        }

        StageRefStore.StageRef stageRef = null;
        string src = spec.Source?.Trim() ?? "";
        if (src.Equals("Base", StringComparison.OrdinalIgnoreCase))
        {
            stageRef = refStore.Base;
        }
        else if (src.Equals("Refiner", StringComparison.OrdinalIgnoreCase))
        {
            stageRef = refStore.Refiner;
        }
        else if (ImageReferenceSyntax.TryParseBase2EditStageIndex(src, out int editStage))
        {
            _ = Base2EditPublishedStageRefs.TryGetStageRef(g, editStage, out stageRef);
        }

        if (stageRef is null)
        {
            if (!string.IsNullOrWhiteSpace(src))
            {
                Logs.Warning($"VideoStages: Unsupported or unresolved clip reference source '{spec.Source}'.");
            }
            return null;
        }

        return ResolveGuideMedia(stageRef, postVideoChain);
    }

    private WGNodeData MaterializeUploadedRefImage(JsonParser.RefSpec spec)
    {
        string material = spec.Data?.Trim();
        if (string.IsNullOrWhiteSpace(material))
        {
            material = spec.UploadFileName?.Trim();
        }
        if (string.IsNullOrWhiteSpace(material))
        {
            Logs.Warning("VideoStages: Upload clip reference is missing inline data and a file name.");
            return null;
        }

        if (material.StartsWith("inputs/", StringComparison.OrdinalIgnoreCase)
            || material.StartsWith("raw/", StringComparison.OrdinalIgnoreCase)
            || material.StartsWith("Starred/", StringComparison.OrdinalIgnoreCase))
        {
            if (g.UserInput?.SourceSession is null)
            {
                Logs.Warning("VideoStages: reference image uses a server-side path but no session is available; cannot load the file.");
                return null;
            }

            try
            {
                material = T2IParamTypes.FilePathToDataString(
                    g.UserInput.SourceSession,
                    material,
                    "for VideoStages reference image");
            }
            catch (SwarmReadableErrorException ex)
            {
                Logs.Warning($"VideoStages: Could not resolve uploaded reference image path '{material}': {ex.Message}");
                return null;
            }
        }

        try
        {
            ImageFile img = ImageFile.FromDataString(material);
            return g.LoadImage(img, "${videostagesrefimage}", false);
        }
        catch
        {
            Logs.Warning("VideoStages: Ignoring invalid clip reference image payload.");
            return null;
        }
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

    private static bool ShouldUseLocalLtxv2Path(WorkflowGenerator.ImageToVideoGenInfo genInfo, WGNodeData sourceMedia)
    {
        return genInfo.VideoModel?.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatLtxv2.ID
            && (sourceMedia?.DataType == WGNodeData.DT_VIDEO || sourceMedia?.DataType == WGNodeData.DT_IMAGE);
    }

    private WGNodeData ResolveGuideMedia(StageRefStore.StageRef guideReference, PostVideoChain postVideoChain)
    {
        if (guideReference?.Media is null)
        {
            return null;
        }
        if (postVideoChain is not null && IsLiveCurrentOutputReference(guideReference.Media, postVideoChain))
        {
            WGNodeData detachedGuideVae = guideReference.Vae ?? postVideoChain.CreateStageInputVae() ?? g.CurrentVae;
            return postVideoChain.CreateDetachedGuideMedia(detachedGuideVae);
        }
        if (guideReference.Media.DataType == WGNodeData.DT_IMAGE || guideReference.Media.DataType == WGNodeData.DT_VIDEO)
        {
            return guideReference.Media;
        }

        WGNodeData guideVae = guideReference.Vae ?? g.CurrentVae;
        if (guideReference.Media.Path is JArray guidePath
            && WorkflowUtils.TryResolveNearestDownstreamDecodeOutput(g.Workflow, guidePath, out JArray decodedGuidePath))
        {
            string rawDataType = guideReference.Media.DataType == WGNodeData.DT_LATENT_VIDEO
                || guideReference.Media.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO
                ? WGNodeData.DT_VIDEO
                : WGNodeData.DT_IMAGE;
            return guideReference.Media.WithPath(decodedGuidePath, rawDataType, guideVae?.Compat);
        }
        return guideReference.Media.AsRawImage(guideVae);
    }

    private static bool IsLiveCurrentOutputReference(WGNodeData guideMedia, PostVideoChain postVideoChain)
    {
        if (guideMedia?.Path is not JArray guidePath || postVideoChain is null)
        {
            return false;
        }

        return JToken.DeepEquals(guidePath, postVideoChain.CurrentOutputMedia?.Path)
            || JToken.DeepEquals(guidePath, postVideoChain.DecodeOutputPath)
            || JToken.DeepEquals(guidePath, postVideoChain.AvLatentPath);
    }

    private static bool ShouldSkipGeneratedGuideReinjection(
        JsonParser.StageSpec stage,
        WGNodeData sourceMedia,
        StageRefStore.StageRef guideReference,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        PostVideoChain postVideoChain)
    {
        return stage.ImageReference == "Generated"
            && postVideoChain?.CanReuseCurrentOutputAsStageInput(sourceMedia) == true
            && IsLiveCurrentOutputReference(guideReference?.Media, postVideoChain)
            && !string.IsNullOrWhiteSpace(guideReference?.Vae?.Compat?.ID)
            && guideReference.Vae.Compat.ID == genInfo.VideoModel?.ModelClass?.CompatClass?.ID;
    }

    private static bool HasOnlyNonPrimaryClipRefs(IReadOnlyList<ResolvedClipRef> clipRefs)
    {
        return clipRefs is { Count: > 0 };
    }

    private WGNodeData PrepareGuideMedia(WGNodeData guideMedia, WGNodeData sourceMedia, bool scaleToSourceSize)
    {
        WGNodeData resolvedGuideMedia = guideMedia ?? sourceMedia;
        if (!scaleToSourceSize)
        {
            return resolvedGuideMedia;
        }

        int targetWidth = sourceMedia.Width ?? g.UserInput.GetImageWidth();
        int targetHeight = sourceMedia.Height ?? g.UserInput.GetImageHeight();
        int currentWidth = resolvedGuideMedia.Width ?? targetWidth;
        int currentHeight = resolvedGuideMedia.Height ?? targetHeight;
        if (currentWidth == targetWidth && currentHeight == targetHeight)
        {
            resolvedGuideMedia.Width = targetWidth;
            resolvedGuideMedia.Height = targetHeight;
            return resolvedGuideMedia;
        }

        string scaleNode = g.CreateNode(NodeTypes.ImageScale, new JObject()
        {
            ["image"] = resolvedGuideMedia.Path,
            ["width"] = targetWidth,
            ["height"] = targetHeight,
            ["upscale_method"] = "lanczos",
            ["crop"] = "disabled"
        });
        resolvedGuideMedia = resolvedGuideMedia.WithPath([scaleNode, 0]);
        resolvedGuideMedia.Width = targetWidth;
        resolvedGuideMedia.Height = targetHeight;
        return resolvedGuideMedia;
    }

    private WGNodeData ApplyStageUpscaleIfNeeded(JsonParser.StageSpec stage, int sectionId)
    {
        WGNodeData source = g.CurrentMedia.AsRawImage(g.CurrentVae);
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
        bool isLtxv2VideoStage = isLtxv2Stage && source.DataType == WGNodeData.DT_VIDEO;
        if (isLtxv2Stage)
        {
            bool supportedLtxUpscale = stage.UpscaleMethod.StartsWith("latent-", StringComparison.OrdinalIgnoreCase)
                || stage.UpscaleMethod.StartsWith("latentmodel-", StringComparison.OrdinalIgnoreCase);
            if (supportedLtxUpscale)
            {
                // Local LTX-V2 stages apply latent upscales later after the stage latent exists.
                g.CurrentMedia = source;
                return source;
            }
            if (isLtxv2VideoStage)
            {
                Logs.Warning($"VideoStages: Stage {stage.Id} uses pixel/model upscale method '{stage.UpscaleMethod}' on an LTX-V2 video stage, which is unsupported. Skipping upscale.");
                g.CurrentMedia = source;
                return source;
            }
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

        Logs.Warning($"VideoStages: Stage {stage.Id} uses unsupported upscale method '{stage.UpscaleMethod}'. Falling back to the unscaled input.");
        g.CurrentMedia = source;
        return source;
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

    private void RetargetExistingAnimationSaves(JArray priorOutputPath, JArray newOutputPath)
    {
        if (priorOutputPath is null
            || newOutputPath is null
            || priorOutputPath.Count != 2
            || newOutputPath.Count != 2
            || JToken.DeepEquals(priorOutputPath, newOutputPath))
        {
            return;
        }

        _ = WorkflowUtils.RetargetInputConnections(
            g.Workflow,
            priorOutputPath,
            newOutputPath,
            connection =>
            {
                if (!string.Equals(connection.InputName, "images", StringComparison.Ordinal))
                {
                    return false;
                }
                if (g.Workflow[connection.NodeId] is not JObject node)
                {
                    return false;
                }
                return $"{node["class_type"]}" == NodeTypes.SwarmSaveAnimationWS;
            });
    }

    private static JArray CopyPath(JArray path)
    {
        if (path is null || path.Count != 2)
        {
            return null;
        }
        return new JArray(path[0], path[1]);
    }
}
