using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages.LTX2;

internal sealed record ResolvedClipRef(WGNodeData Image, JsonParser.RefSpec Spec, double Strength);

internal sealed class LtxStageExecutor(WorkflowGenerator g)
{
    private bool _needsLtxvCropGuidesAfterSampler;

    private const int ImgCompression = 18;
    private const double DefaultGuideMergeStrength = 1.0;
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
            foreach (Action<WorkflowGenerator.ImageToVideoGenInfo> handler in WorkflowGenerator.AltImageToVideoPreHandlers)
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

            foreach (Action<WorkflowGenerator.ImageToVideoGenInfo> handler in WorkflowGenerator.AltImageToVideoPostHandlers)
            {
                handler(genInfo);
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

        genInfo.VideoFPS ??= DefaultVideoFps;
        genInfo.Frames ??= DefaultVideoFrameCount;
        genInfo.DefaultCFG = DefaultVideoCfg;
        genInfo.HadSpecialCond = true;
        genInfo.DefaultSampler = DefaultVideoSampler;
        genInfo.DefaultScheduler = DefaultVideoScheduler;
        stageLatent = ApplyStageUpscaleIfNeeded(stage, genInfo, stageLatent, sourceMedia);
        stageLatent = ApplyClipReferenceInplaceMerges(genInfo, stageLatent, clipRefs);

        if (skipGuideReinjection)
        {
            g.CurrentMedia = stageLatent;
        }
        else
        {
            JArray preprocessedGuidePath = ResolvePreprocessedGuidePath(guideMedia.Path);
            string imgToVideoNode = g.CreateNode(LtxNodeTypes.LTXVImgToVideoInplace, new JObject()
            {
                ["vae"] = genInfo.Vae.Path,
                ["image"] = preprocessedGuidePath,
                ["latent"] = stageLatent.Path,
                ["strength"] = guideMergeStrength,
                ["bypass"] = false
            });
            g.CurrentMedia = stageLatent.WithPath([imgToVideoNode, 0], WGNodeData.DT_LATENT_VIDEO, genInfo.Model.Compat);
        }

        string conditioningNode = g.CreateNode(LtxNodeTypes.LTXVConditioning, new JObject()
        {
            ["positive"] = genInfo.PosCond,
            ["negative"] = genInfo.NegCond,
            ["frame_rate"] = genInfo.VideoFPS
        });
        genInfo.PosCond = [conditioningNode, 0];
        genInfo.NegCond = [conditioningNode, 1];

        ApplyClipReferenceAddGuides(genInfo, clipRefs);
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

            JArray preprocessed = ResolvePreprocessedGuidePath(clipRef.Image.Path);
            string imgToVideoNode = g.CreateNode(LtxNodeTypes.LTXVImgToVideoInplace, new JObject()
            {
                ["vae"] = genInfo.Vae.Path,
                ["image"] = preprocessed,
                ["latent"] = stageLatent.Path,
                ["strength"] = clipRef.Strength,
                ["bypass"] = false
            });
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

            JArray preprocessed = ResolvePreprocessedGuidePath(clipRef.Image.Path);
            int frameIdx = ComputeLtxvAddGuideFrameIndex(clipRef.Spec);
            string addGuideNode = g.CreateNode(LtxNodeTypes.LTXVAddGuide, new JObject()
            {
                ["positive"] = genInfo.PosCond,
                ["negative"] = genInfo.NegCond,
                ["vae"] = genInfo.Vae.Path,
                ["latent"] = g.CurrentMedia.Path,
                ["image"] = preprocessed,
                ["frame_idx"] = frameIdx,
                ["strength"] = clipRef.Strength
            });
            _needsLtxvCropGuidesAfterSampler = true;
            genInfo.PosCond = [addGuideNode, 0];
            genInfo.NegCond = [addGuideNode, 1];
            g.CurrentMedia = g.CurrentMedia.WithPath([addGuideNode, 2], WGNodeData.DT_LATENT_VIDEO, genInfo.Model.Compat);
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

        Logs.Warning($"VideoStages: Stage {stage.Id} uses unsupported LTX upscale method '{stage.UpscaleMethod}'. Ignoring upscale.");
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
        string upsamplerNode = g.CreateNode(LtxNodeTypes.LTXVLatentUpsampler, new JObject()
        {
            ["vae"] = genInfo.Vae.Path,
            ["samples"] = stageLatent.Path,
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
        LtxPostVideoChain postVideoChain)
    {
        genInfo.StartStep = (int)Math.Floor(stage.Steps * (1 - stage.Control));

        if (RootVideoStageTakeover.ShouldReplaceTextToVideoRootStage(g, stage))
        {
            return CreateEmptyVideoLatent(genInfo, stage, sourceMedia);
        }

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
        if (sourceMedia?.DataType == WGNodeData.DT_IMAGE)
        {
            int width = Math.Max(sourceMedia.Width ?? g.UserInput.GetImageWidth(), 16);
            int height = Math.Max(sourceMedia.Height ?? g.UserInput.GetImageHeight(), 16);
            JArray audioLengthFrames = null;
            WGNodeData attachedAudio = sourceMedia.AttachedAudio;
            if (ShouldMatchStageLengthToAudio(stage)
                && attachedAudio?.Path is JToken audioPath)
            {
                int fps = ResolveStageFps(genInfo, sourceMedia);
                JToken lengthFramesAudioSource = ResolveLengthToFramesAudioSource(audioPath);
                string lengthToFrames = g.CreateNode(NodeTypes.AudioLengthToFrames, new JObject()
                {
                    ["audio"] = lengthFramesAudioSource,
                    ["frame_rate"] = fps
                });
                audioLengthFrames = new JArray(lengthToFrames, 1);
                attachedAudio = new WGNodeData(
                    new JArray(lengthToFrames, 0),
                    g,
                    WGNodeData.DT_AUDIO,
                    g.CurrentAudioVae?.Compat ?? attachedAudio.Compat);
            }
            JToken latentLength = audioLengthFrames is null ? new JValue(genInfo.Frames.Value) : audioLengthFrames;
            string emptyLatent = g.CreateNode(LtxNodeTypes.EmptyLTXVLatentVideo, new JObject()
            {
                ["width"] = width,
                ["height"] = height,
                ["length"] = latentLength,
                ["batch_size"] = 1
            });
            WGNodeData imageStageLatent = new([emptyLatent, 0], g, WGNodeData.DT_LATENT_VIDEO, genInfo.Model.Compat)
            {
                Width = width,
                Height = height,
                Frames = audioLengthFrames is null ? genInfo.Frames.Value : null,
                FPS = genInfo.VideoFPS
            };
            imageStageLatent.AttachedAudio = attachedAudio;
            return imageStageLatent.EnsureHasAudioIfNeeded(genInfo.Vae, g.CurrentAudioVae);
        }

        if (CanReuseDecodedVideoLatent(sourceMedia, genInfo))
        {
            WGNodeData reusedLatent = sourceMedia.AsLatentImage(genInfo.Vae);
            reusedLatent.Frames = Math.Min(genInfo.Frames.Value, reusedLatent.Frames ?? int.MaxValue);
            return reusedLatent.EnsureHasAudioIfNeeded(genInfo.Vae, g.CurrentAudioVae);
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

    private WGNodeData CreateEmptyVideoLatent(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        JsonParser.StageSpec stage,
        WGNodeData sourceMedia)
    {
        int width = Math.Max(sourceMedia?.Width ?? g.UserInput.GetImageWidth(), 16);
        int height = Math.Max(sourceMedia?.Height ?? g.UserInput.GetImageHeight(), 16);
        int frames = genInfo.Frames ?? sourceMedia?.Frames ?? DefaultVideoFrameCount;
        WGNodeData attachedAudio = sourceMedia?.AttachedAudio;
        JArray audioLengthFrames = null;
        if (ShouldMatchStageLengthToAudio(stage)
            && attachedAudio?.Path is JToken audioPath)
        {
            int fps = ResolveStageFps(genInfo, sourceMedia);
            JToken lengthFramesAudioSource = ResolveLengthToFramesAudioSource(audioPath);
            string lengthToFrames = g.CreateNode(NodeTypes.AudioLengthToFrames, new JObject()
            {
                ["audio"] = lengthFramesAudioSource,
                ["frame_rate"] = fps
            });
            audioLengthFrames = new JArray(lengthToFrames, 1);
            attachedAudio = new WGNodeData(
                new JArray(lengthToFrames, 0),
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae?.Compat ?? attachedAudio.Compat);
        }

        JToken latentLength = audioLengthFrames is null ? new JValue(frames) : audioLengthFrames;
        string emptyLatent = g.CreateNode(LtxNodeTypes.EmptyLTXVLatentVideo, new JObject()
        {
            ["width"] = width,
            ["height"] = height,
            ["length"] = latentLength,
            ["batch_size"] = 1
        });
        WGNodeData stageLatent = new([emptyLatent, 0], g, WGNodeData.DT_LATENT_VIDEO, genInfo.Model.Compat)
        {
            Width = width,
            Height = height,
            Frames = audioLengthFrames is null ? frames : null,
            FPS = genInfo.VideoFPS,
            AttachedAudio = attachedAudio
        };
        return stageLatent.EnsureHasAudioIfNeeded(genInfo.Vae, g.CurrentAudioVae);
    }

    private static bool CanReuseDecodedVideoLatent(
        WGNodeData sourceMedia,
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        (string sourceType, JObject sourceInputs) = sourceMedia?.SourceNodeData ?? (null, null);
        if (sourceMedia?.DataType != WGNodeData.DT_VIDEO
            || genInfo?.Vae?.Path is not JArray vaePath
            || vaePath.Count != 2
            || !genInfo.Frames.HasValue
            || sourceMedia.Frames is int sourceFrames && sourceFrames > genInfo.Frames.Value)
        {
            return false;
        }
        return (sourceType == NodeTypes.VAEDecode || sourceType == NodeTypes.VAEDecodeTiled)
            && sourceInputs?["samples"] is JArray
            && sourceInputs["vae"] is JArray decodeVaePath
            && decodeVaePath.Count == 2
            && JToken.DeepEquals(decodeVaePath, vaePath);
    }

    private static bool ShouldMatchStageLengthToAudio(JsonParser.StageSpec stage)
    {
        if (!stage.ClipLengthFromAudio)
        {
            return false;
        }
        if (string.Equals(stage.ClipAudioSource, VideoStagesExtension.AudioSourceUpload, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return AudioStageDetector.TryParseAceStepFunAudioSource(stage.ClipAudioSource, out _);
    }

    private int ResolveStageFps(WorkflowGenerator.ImageToVideoGenInfo genInfo, WGNodeData sourceMedia)
    {
        int? fps = genInfo.VideoFPS ?? sourceMedia.FPS;
        if (fps.HasValue && fps.Value > 0)
        {
            return fps.Value;
        }
        fps = new JsonParser(g).ResolveFps();
        return fps.HasValue && fps.Value > 0 ? fps.Value : DefaultVideoFps;
    }

    private JToken ResolveLengthToFramesAudioSource(JToken rawAudioPath)
    {
        if (rawAudioPath is not JArray rawRef || rawRef.Count != 2)
        {
            return rawAudioPath;
        }

        foreach (JProperty property in g.Workflow.Properties())
        {
            if (property.Value is not JObject node
                || $"{node["class_type"]}" != NodeTypes.SwarmEnsureAudio
                || node["inputs"] is not JObject inputs
                || inputs["audio"] is not JArray audioInput
                || audioInput.Count != 2)
            {
                continue;
            }
            if (ConnectionRefsEqual(audioInput, rawRef))
            {
                return new JArray(property.Name, 0);
            }
        }

        if (!IsSwarmLoadAudioB64Output(rawRef))
        {
            return rawAudioPath;
        }

        string ensured = g.CreateNode(NodeTypes.SwarmEnsureAudio, new JObject()
        {
            ["audio"] = rawRef,
            ["target_duration"] = 0.1
        });
        return new JArray(ensured, 0);
    }

    private bool IsSwarmLoadAudioB64Output(JArray rawRef)
    {
        string sourceId = $"{rawRef[0]}";
        if (!g.Workflow.TryGetValue(sourceId, out JToken token) || token is not JObject node)
        {
            return false;
        }

        return $"{node["class_type"]}" == NodeTypes.SwarmLoadAudioB64;
    }

    private static bool ConnectionRefsEqual(JToken left, JToken right)
    {
        if (left is not JArray la || right is not JArray ra || la.Count != 2 || ra.Count != 2)
        {
            return false;
        }

        return $"{la[0]}" == $"{ra[0]}" && la[1].Value<int>() == ra[1].Value<int>();
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

    private JArray ResolvePreprocessedGuidePath(JArray guideImagePath)
    {
        if (TryFindReusablePreprocessOutput(guideImagePath, out JArray reusedPath))
        {
            return reusedPath;
        }

        JArray scaledGuidePath = EnsureClipResolutionBeforeLtxvPreprocess(guideImagePath);
        if (!JToken.DeepEquals(scaledGuidePath, guideImagePath)
            && TryFindReusablePreprocessOutput(scaledGuidePath, out reusedPath))
        {
            return reusedPath;
        }

        string preprocessNode = g.CreateNode(LtxNodeTypes.LTXVPreprocess, new JObject()
        {
            ["image"] = scaledGuidePath,
            ["img_compression"] = ImgCompression
        });
        return new JArray(preprocessNode, 0);
    }

    private JArray EnsureClipResolutionBeforeLtxvPreprocess(JArray guideImagePath)
    {
        if (guideImagePath is null || guideImagePath.Count != 2)
        {
            return guideImagePath;
        }

        if (!RootVideoStageResizer.TryGetRootStageResolution(g, out int targetW, out int targetH))
        {
            targetW = Math.Max(16, g.UserInput.GetImageWidth());
            targetH = Math.Max(16, g.UserInput.GetImageHeight());
        }

        if (TryPathEndsWithClipResolutionImageScale(g.Workflow, guideImagePath, targetW, targetH))
        {
            return guideImagePath;
        }

        string scaleNode = g.CreateNode(NodeTypes.ImageScale, new JObject()
        {
            ["image"] = guideImagePath,
            ["width"] = targetW,
            ["height"] = targetH,
            ["upscale_method"] = "lanczos",
            ["crop"] = "center"
        });
        return new JArray(scaleNode, 0);
    }

    private static bool TryPathEndsWithClipResolutionImageScale(
        JObject workflow,
        JArray imagePath,
        int targetW,
        int targetH)
    {
        if (workflow is null
            || imagePath is not { Count: 2 }
            || !workflow.TryGetValue($"{imagePath[0]}", out JToken token)
            || token is not JObject node
            || $"{node["class_type"]}" != NodeTypes.ImageScale
            || node["inputs"] is not JObject inputs)
        {
            return false;
        }

        int? width = inputs.Value<int?>("width");
        int? height = inputs.Value<int?>("height");
        if (width != targetW || height != targetH)
        {
            return false;
        }

        inputs["crop"] = "center";
        return true;
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
                    && consumerType == LtxNodeTypes.LTXVPreprocess
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
            && $"{node["class_type"]}" == LtxNodeTypes.LTXVPreprocess
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

        if (_needsLtxvCropGuidesAfterSampler)
        {
            string postSamplerCrop = g.CreateNode(LtxNodeTypes.LTXVCropGuides, new JObject()
            {
                ["positive"] = genInfo.PosCond,
                ["negative"] = genInfo.NegCond,
                ["latent"] = g.CurrentMedia.Path
            });
            genInfo.PosCond = [postSamplerCrop, 0];
            genInfo.NegCond = [postSamplerCrop, 1];
            g.CurrentMedia = g.CurrentMedia.WithPath([postSamplerCrop, 2], null, genInfo.Model.Compat);
            _needsLtxvCropGuidesAfterSampler = false;
        }

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
        LtxPostVideoChain postVideoChain)
    {
        int outputWidth = g.CurrentMedia?.Width ?? sourceMedia.Width ?? g.UserInput.GetImageWidth();
        int outputHeight = g.CurrentMedia?.Height ?? sourceMedia.Height ?? g.UserInput.GetImageHeight();
        bool splicedIntoNativeChain = postVideoChain is not null;
        bool parallelMultiClip = g.NodeHelpers.TryGetValue(MultiClipParallelWorkflowFlags.NodeHelperKey, out string parallelFlag)
            && string.Equals(parallelFlag, "1", StringComparison.Ordinal);
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
                ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, postVideoChain.CurrentOutputMedia.Frames, postVideoChain.CurrentOutputMedia.FPS);
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
            string trimNode = g.CreateNode("SwarmTrimFrames", new JObject()
            {
                ["image"] = g.CurrentMedia.Path,
                ["trim_start"] = trimStartFrames,
                ["trim_end"] = trimEndFrames
            });
            g.CurrentMedia = g.CurrentMedia.WithPath([trimNode, 0]);
            if (splicedIntoNativeChain && !postVideoChain.HasPostDecodeWrappers && !parallelMultiClip)
            {
                postVideoChain.RetargetAnimationSaves(g.CurrentMedia.Path);
            }
            ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, genInfo.Frames, genInfo.VideoFPS);
        }

        g.CurrentVae = genInfo.Vae;
    }

    private void AttachDecodedLtxAudioFromCurrentVideo()
    {
        if (g.CurrentMedia?.Path is not JArray currentPath
            || currentPath.Count != 2
            || g.Workflow[$"{currentPath[0]}"] is not JObject decodeNode
            || decodeNode["inputs"] is not JObject decodeInputs)
        {
            return;
        }

        string decodeType = $"{decodeNode["class_type"]}";
        if (decodeType != NodeTypes.VAEDecode && decodeType != NodeTypes.VAEDecodeTiled)
        {
            return;
        }

        JArray videoSamples = decodeInputs["samples"] as JArray
            ?? decodeInputs["latent"] as JArray
            ?? decodeInputs["latents"] as JArray;
        if (videoSamples is null
            || videoSamples.Count != 2
            || g.Workflow[$"{videoSamples[0]}"] is not JObject separateNode
            || $"{separateNode["class_type"]}" != LtxNodeTypes.LTXVSeparateAVLatent
            || !TryResolveAudioVaePath(out JArray audioVaePath))
        {
            return;
        }

        string audioDecode = g.CreateNode(LtxNodeTypes.LTXVAudioVAEDecode, new JObject()
        {
            ["samples"] = new JArray(videoSamples[0], 1),
            ["audio_vae"] = new JArray(audioVaePath[0], audioVaePath[1])
        });
        g.CurrentMedia.AttachedAudio = new WGNodeData([audioDecode, 0], g, WGNodeData.DT_AUDIO, g.CurrentAudioVae?.Compat);
    }

    private bool TryResolveAudioVaePath(out JArray audioVaePath)
    {
        audioVaePath = null;
        if (g.CurrentAudioVae?.Path is JArray currentAudioVaePath && currentAudioVaePath.Count == 2)
        {
            audioVaePath = new JArray(currentAudioVaePath[0], currentAudioVaePath[1]);
            return true;
        }

        foreach (JProperty property in g.Workflow.Properties())
        {
            if (property.Value is not JObject node
                || $"{node["class_type"]}" != LtxNodeTypes.LTXVAudioVAEDecode
                || node["inputs"]?["audio_vae"] is not JArray foundAudioVaePath
                || foundAudioVaePath.Count != 2)
            {
                continue;
            }

            audioVaePath = new JArray(foundAudioVaePath[0], foundAudioVaePath[1]);
            return true;
        }
        return false;
    }

    private void ApplyCurrentMediaOutputMetadata(int width, int height, int? frames, int? fps)
    {
        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
        g.CurrentMedia.Frames = frames ?? g.CurrentMedia.Frames;
        g.CurrentMedia.FPS = fps ?? g.CurrentMedia.FPS;
    }
}
