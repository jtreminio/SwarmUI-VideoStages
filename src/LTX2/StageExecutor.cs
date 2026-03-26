using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages;

namespace VideoStages.LTX2;

/// <summary>
/// Local LTXV2 stage execution for VideoStages. This is intentionally isolated so the
/// rest of the extension can stay on the native CreateImageToVideo path.
/// </summary>
internal sealed class StageExecutor(WorkflowGenerator g)
{
    private const int ImgCompression = 18;
    private const int DefaultVideoFps = 24;
    private const int DefaultVideoFrameCount = 97;
    private const double DefaultVideoCfg = 3;
    private const string DefaultVideoSampler = "euler";
    private const string DefaultVideoScheduler = "normal";

    public void RunStage(
        JsonParser.StageSpec stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        bool skipGuideReinjection,
        PostVideoChain postVideoChain)
    {
        postVideoChain?.AttachSourceAudio(sourceMedia);

        g.IsImageToVideo = true;
        try
        {
            foreach (Action<WorkflowGenerator.ImageToVideoGenInfo> handler in WorkflowGenerator.AltImageToVideoPreHandlers)
            {
                handler(genInfo);
            }

            PrepareModelAndConditioning(genInfo, sourceMedia);
            PrepareConditioning(genInfo, stage, sourceMedia, guideMedia, skipGuideReinjection, postVideoChain);
            genInfo.VideoCFG ??= genInfo.DefaultCFG;

            foreach (Action<WorkflowGenerator.ImageToVideoGenInfo> handler in WorkflowGenerator.AltImageToVideoPostHandlers)
            {
                handler(genInfo);
            }

            ExecuteSampler(genInfo);
            FinalizeOutput(genInfo, sourceMedia, postVideoChain);
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

        string posCondNode = g.CreateNode(NodeTypes.SwarmClipTextEncodeAdvanced, new JObject()
        {
            ["clip"] = clip.Path,
            ["steps"] = steps,
            ["prompt"] = positivePrompt,
            ["width"] = width,
            ["height"] = height,
            ["target_width"] = width,
            ["target_height"] = height,
            ["guidance"] = guidance
        });
        string negCondNode = g.CreateNode(NodeTypes.SwarmClipTextEncodeAdvanced, new JObject()
        {
            ["clip"] = clip.Path,
            ["steps"] = steps,
            ["prompt"] = negativePrompt,
            ["width"] = width,
            ["height"] = height,
            ["target_width"] = width,
            ["target_height"] = height,
            ["guidance"] = guidance
        });
        genInfo.PosCond = new JArray(posCondNode, 0);
        genInfo.NegCond = new JArray(negCondNode, 0);
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
        PostVideoChain postVideoChain)
    {
        WGNodeData stageLatent = BuildStageLatent(genInfo, stage, sourceMedia, postVideoChain);
        if (stageLatent is null)
        {
            genInfo.PrepFullCond(g, guideMedia);
            if (genInfo.AltLatent is not null)
            {
                genInfo.AltLatent(genInfo);
            }
            return;
        }

        genInfo.VideoFPS ??= DefaultVideoFps;
        genInfo.Frames ??= DefaultVideoFrameCount;
        genInfo.DefaultCFG = DefaultVideoCfg;
        genInfo.HadSpecialCond = true;
        genInfo.DefaultSampler = DefaultVideoSampler;
        genInfo.DefaultScheduler = DefaultVideoScheduler;
        stageLatent = ApplyStageUpscaleIfNeeded(stage, genInfo, stageLatent, sourceMedia);

        if (skipGuideReinjection)
        {
            g.CurrentMedia = stageLatent;
        }
        else
        {
            JArray preprocessedGuidePath = ResolvePreprocessedGuidePath(guideMedia.Path);
            string imgToVideoNode = g.CreateNode(NodeTypes.LTXVImgToVideoInplace, new JObject()
            {
                ["vae"] = genInfo.Vae.Path,
                ["image"] = preprocessedGuidePath,
                ["latent"] = stageLatent.Path,
                ["strength"] = g.UserInput.Get(
                    VideoStagesExtension.LTXVImgToVideoInplaceStrength,
                    VideoStagesExtension.DefaultLTXVImgToVideoInplaceStrength),
                ["bypass"] = false
            });
            g.CurrentMedia = stageLatent.WithPath([imgToVideoNode, 0], WGNodeData.DT_LATENT_VIDEO, genInfo.Model.Compat);
        }

        string conditioningNode = g.CreateNode(NodeTypes.LTXVConditioning, new JObject()
        {
            ["positive"] = genInfo.PosCond,
            ["negative"] = genInfo.NegCond,
            ["frame_rate"] = genInfo.VideoFPS
        });
        genInfo.PosCond = [conditioningNode, 0];
        genInfo.NegCond = [conditioningNode, 1];
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

        Logs.Warning($"VideoStages: Stage {stage.Id} uses unsupported LTX-V2 video upscale method '{stage.UpscaleMethod}'. Skipping upscale.");
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
        string loaderNode = g.CreateNode(NodeTypes.LatentUpscaleModelLoader, new JObject()
        {
            ["model_name"] = modelName
        });
        string cropGuidesNode = g.CreateNode(NodeTypes.LTXVCropGuides, new JObject()
        {
            ["positive"] = genInfo.PosCond,
            ["negative"] = genInfo.NegCond,
            ["latent"] = stageLatent.Path
        });
        genInfo.PosCond = [cropGuidesNode, 0];
        genInfo.NegCond = [cropGuidesNode, 1];
        string upsamplerNode = g.CreateNode(NodeTypes.LTXVLatentUpsampler, new JObject()
        {
            ["vae"] = genInfo.Vae.Path,
            ["samples"] = new JArray(cropGuidesNode, 2),
            ["upscale_model"] = new JArray(loaderNode, 0)
        });
        stageLatent = stageLatent.WithPath([upsamplerNode, 0], WGNodeData.DT_LATENT_VIDEO);
        stageLatent.Width = width;
        stageLatent.Height = height;
        return stageLatent;
    }

    private WGNodeData ApplyLatentUpscale(
        WGNodeData stageLatent,
        string latentMethod,
        double scaleBy,
        int width,
        int height)
    {
        string latentUpscaleNode = g.CreateNode(NodeTypes.LatentUpscaleBy, new JObject()
        {
            ["samples"] = stageLatent.Path,
            ["upscale_method"] = latentMethod,
            ["scale_by"] = scaleBy
        });
        stageLatent = stageLatent.WithPath([latentUpscaleNode, 0], WGNodeData.DT_LATENT_VIDEO);
        stageLatent.Width = width;
        stageLatent.Height = height;
        return stageLatent;
    }

    private WGNodeData BuildStageLatent(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        JsonParser.StageSpec stage,
        WGNodeData sourceMedia,
        PostVideoChain postVideoChain)
    {
        genInfo.StartStep = (int)Math.Floor(stage.Steps * (1 - stage.Control));

        if (postVideoChain?.CanReuseCurrentOutputAsStageInput(sourceMedia) == true)
        {
            WGNodeData nativeStageInput = postVideoChain.CreateStageInput();
            WGNodeData nativeVideoLatent = nativeStageInput.AsLatentImage(genInfo.Vae);
            postVideoChain.AttachSourceAudio(nativeVideoLatent);
            return nativeVideoLatent.EnsureHasAudioIfNeeded(genInfo.Vae, g.CurrentAudioVae);
        }

        if (!genInfo.Frames.HasValue)
        {
            return null;
        }

        WGNodeData sourceSnapshot = sourceMedia;
        if (postVideoChain is not null && ReferencesCurrentOutputPath(sourceMedia, postVideoChain))
        {
            sourceSnapshot = postVideoChain.CreateDetachedGuideMedia(genInfo.Vae);
        }

        string fromBatch = g.CreateNode("ImageFromBatch", new JObject()
        {
            ["image"] = sourceSnapshot.Path,
            ["batch_index"] = 0,
            ["length"] = genInfo.Frames.Value
        });
        WGNodeData stageVideoInput = sourceSnapshot.WithPath([fromBatch, 0]);
        stageVideoInput.Frames = Math.Min(genInfo.Frames.Value, stageVideoInput.Frames ?? int.MaxValue);
        WGNodeData encodedLatent = stageVideoInput.AsLatentImage(genInfo.Vae);
        return encodedLatent.EnsureHasAudioIfNeeded(genInfo.Vae, g.CurrentAudioVae);
    }

    private static bool ReferencesCurrentOutputPath(WGNodeData media, PostVideoChain postVideoChain)
    {
        if (media?.Path is not JArray mediaPath || postVideoChain is null)
        {
            return false;
        }

        return JToken.DeepEquals(mediaPath, postVideoChain.CurrentOutputMedia?.Path)
            || JToken.DeepEquals(mediaPath, postVideoChain.DecodeOutputPath);
    }

    private JArray ResolvePreprocessedGuidePath(JArray guideImagePath)
    {
        if (TryFindReusablePreprocessOutput(guideImagePath, out JArray reusedPath))
        {
            return reusedPath;
        }

        string preprocessNode = g.CreateNode(NodeTypes.LTXVPreprocess, new JObject()
        {
            ["image"] = guideImagePath,
            ["img_compression"] = ImgCompression
        });
        return new JArray(preprocessNode, 0);
    }

    private bool TryFindReusablePreprocessOutput(JArray guideImagePath, out JArray preprocessOutputPath)
    {
        preprocessOutputPath = null;
        if (guideImagePath is null || guideImagePath.Count != 2)
        {
            return false;
        }

        if (TryResolveReusablePreprocessNode(guideImagePath, out string preprocessNodeId))
        {
            preprocessOutputPath = new JArray(preprocessNodeId, 0);
            return true;
        }

        Queue<JArray> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(new JArray(guideImagePath[0], guideImagePath[1]));
        while (pending.Count > 0)
        {
            JArray currentPath = pending.Dequeue();
            string currentKey = $"{currentPath[0]}::{currentPath[1]}";
            if (!visited.Add(currentKey))
            {
                continue;
            }

            IReadOnlyList<WorkflowInputConnection> consumers = WorkflowUtils.FindInputConnections(g.Workflow, currentPath);
            foreach (WorkflowInputConnection consumer in consumers)
            {
                if (!g.Workflow.TryGetValue(consumer.NodeId, out JToken consumerToken) || consumerToken is not JObject consumerNode)
                {
                    continue;
                }

                string consumerType = $"{consumerNode["class_type"]}";
                if (consumer.InputName == "image"
                    && consumerType == NodeTypes.LTXVPreprocess
                    && HasMatchingImgCompression(consumerNode))
                {
                    preprocessOutputPath = new JArray(consumer.NodeId, 0);
                    return true;
                }

                if (CanTraverseReusableGuideImagePath(consumer, consumerType))
                {
                    pending.Enqueue(new JArray(consumer.NodeId, 0));
                }
            }
        }

        return false;
    }

    private static bool CanTraverseReusableGuideImagePath(WorkflowInputConnection consumer, string consumerType)
    {
        return consumer.InputName == "image"
            && consumerType == NodeTypes.ImageScale;
    }

    private bool TryResolveReusablePreprocessNode(JArray imagePath, out string preprocessNodeId)
    {
        preprocessNodeId = $"{imagePath[0]}";
        if (!g.Workflow.TryGetValue(preprocessNodeId, out JToken token) || token is not JObject node)
        {
            return false;
        }

        return $"{imagePath[1]}" == "0"
            && $"{node["class_type"]}" == NodeTypes.LTXVPreprocess
            && HasMatchingImgCompression(node);
    }

    private static bool HasMatchingImgCompression(JObject preprocessNode)
    {
        if (preprocessNode?["inputs"] is not JObject inputs)
        {
            return false;
        }
        if (inputs["img_compression"]?.Type != JTokenType.Integer)
        {
            return false;
        }
        return inputs.Value<int>("img_compression") == ImgCompression;
    }

    private void ExecuteSampler(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        string previewType = g.UserInput.Get(ComfyUIBackendExtension.VideoPreviewType, "animate");
        string explicitSampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: genInfo.ContextID, includeBase: false);
        string explicitScheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: genInfo.ContextID, includeBase: false);

        g.CurrentMedia = g.CurrentMedia.AsSamplingLatent(genInfo.Vae, g.CurrentAudioVae);
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

        if (genInfo.DoFirstFrameLatentSwap is not null)
        {
            string replaceNode = g.CreateNode("ReplaceVideoLatentFrames", new JObject()
            {
                ["destination"] = g.CurrentMedia.Path,
                ["source"] = genInfo.DoFirstFrameLatentSwap,
                ["index"] = 0
            });
            g.CurrentMedia = g.CurrentMedia.WithPath([replaceNode, 0]);

            string normalizeNode = g.CreateNode("NormalizeVideoLatentStart", new JObject()
            {
                ["latent"] = g.CurrentMedia.Path,
                ["start_frame_count"] = 4,
                ["reference_frame_count"] = 5
            });
            g.CurrentMedia = g.CurrentMedia.WithPath([normalizeNode, 0]);
        }
    }

    private void FinalizeOutput(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia,
        PostVideoChain postVideoChain)
    {
        int outputWidth = g.CurrentMedia?.Width ?? sourceMedia.Width ?? g.UserInput.GetImageWidth();
        int outputHeight = g.CurrentMedia?.Height ?? sourceMedia.Height ?? g.UserInput.GetImageHeight();
        bool splicedIntoNativeChain = postVideoChain is not null;
        if (splicedIntoNativeChain)
        {
            postVideoChain.SpliceCurrentOutput(genInfo.Vae);
            if (postVideoChain.HasPostDecodeWrappers)
            {
                ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, postVideoChain.CurrentOutputMedia.Frames, postVideoChain.CurrentOutputMedia.FPS);
            }
            else
            {
                ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, genInfo.Frames, genInfo.VideoFPS);
            }
        }
        else
        {
            g.CurrentMedia = g.CurrentMedia.AsRawImage(genInfo.Vae);
            ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, genInfo.Frames, genInfo.VideoFPS);
        }

        bool shouldApplyTrim = (g.UserInput.TryGet(T2IParamTypes.TrimVideoStartFrames, out _)
            || g.UserInput.TryGet(T2IParamTypes.TrimVideoEndFrames, out _))
            && !(splicedIntoNativeChain && postVideoChain.HasPostDecodeWrappers);
        if (shouldApplyTrim)
        {
            string trimNode = g.CreateNode("SwarmTrimFrames", new JObject()
            {
                ["image"] = g.CurrentMedia.Path,
                ["trim_start"] = g.UserInput.Get(T2IParamTypes.TrimVideoStartFrames, 0),
                ["trim_end"] = g.UserInput.Get(T2IParamTypes.TrimVideoEndFrames, 0)
            });
            g.CurrentMedia = g.CurrentMedia.WithPath([trimNode, 0]);
            if (splicedIntoNativeChain && !postVideoChain.HasPostDecodeWrappers)
            {
                postVideoChain.RetargetAnimationSaves(g.CurrentMedia.Path);
            }
            ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, genInfo.Frames, genInfo.VideoFPS);
        }

        g.CurrentVae = genInfo.Vae;
    }

    private void ApplyCurrentMediaOutputMetadata(int width, int height, int? frames, int? fps)
    {
        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
        g.CurrentMedia.Frames = frames ?? g.CurrentMedia.Frames;
        g.CurrentMedia.FPS = fps ?? g.CurrentMedia.FPS;
    }
}
