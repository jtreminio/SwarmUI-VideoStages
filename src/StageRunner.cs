using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Execution;
using VideoStages.LTX2;
using VideoStages.Planning;

namespace VideoStages;

internal class StageRunner(
    WorkflowGenerator g,
    LtxManager ltxManager)
{
    public virtual RuntimeArtifact RunStage(
        StagePlan stage,
        int sectionId,
        StageRefStore.StageRef guideReference,
        StageRefStore refStore,
        ClipContext clipContext,
        StageExecutionOptions executionOptions)
    {
        if (g.CurrentMedia is null)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: stage {stage.StageId} has no input media.");
        }

        ClipSpec clip = clipContext.Clip;
        if (stage.Execution == StageExecutionMode.Passthrough && !ReplacesTextToVideoRoot(stage, clipContext))
        {
            RunPassthroughStage(stage, sectionId, clipContext);
            return CaptureOutput(stage);
        }

        using ParamSnapshot promptLoraScope = PromptParser.ApplyLoraScope(g.UserInput, clip.Id, sectionId);
        using ParamSnapshot loraScope = ApplyStageLoras(g.UserInput, stage);

        StageFrame stageFrame = PrepareStage(stage, sectionId, clipContext, executionOptions);
        WorkflowGenerator.ImageToVideoGenInfo genInfo = stageFrame.GenInfo;
        using IDisposable controlNetScope = AltImageToVideoScope.Post(genInfo, currentGenInfo =>
        {
            bool needsCrop = new IcLoraApplicator(g).ApplyIcLoras(
                currentGenInfo,
                clip,
                stage.Core.ControlNetStrength,
                clipContext.Clip.Frames,
                clip.ClipLengthFromControlNet,
                stage.ClipStageRawIndex,
                ResolveIcLoraStageInput(clip, stageFrame));
            if (needsCrop)
            {
                stageFrame.NeedsCropGuidesAfterSampler = true;
            }
            new VoiceRefApplicator(g).ApplyVoiceRefTokens(
                currentGenInfo,
                clip,
                stageFrame,
                clipContext.IsFirstStage(stage));
        });

        ltxManager.RunStage(
            guideReference,
            refStore,
            genInfo,
            stageFrame,
            stageFrame.SourceMedia,
            stageFrame.PriorOutputPath,
            stageFrame.PostVideoChain);
        return CaptureOutput(stage);
    }

    /// <summary>
    /// A passthrough stage's output IS its input (plus any pixel/model upscale): emit only the
    /// pixel transform and skip the generation scaffold (loaders, conditioning, IC-LoRA patch,
    /// audio latent, zero-step sampler) — the host's post-cleanup collapses that scaffold only
    /// partially, leaving dead nodes (e.g. a dangling IC-LoRA loader) and a lossy
    /// encode/preprocess/decode roundtrip in the live pixel path.
    /// </summary>
    private void RunPassthroughStage(StagePlan stage, int sectionId, ClipContext clipContext)
    {
        ltxManager.PrepareReusableAudio(clipContext, stage);
        LtxPostVideoChainCapture postVideoChain = ltxManager.TryCapturePostVideoChain(clipContext, stage);
        _ = ApplyStageUpscaleIfNeeded(clipContext, stage, sectionId, postVideoChain);
    }

    private bool ReplacesTextToVideoRoot(StagePlan stage, ClipContext clipContext)
    {
        if (g.RequireLtxVideoExecutionPlanContext().Plan.Root.Use
            == Planning.RootUse.GlobalRefineReplacement)
        {
            return false;
        }
        return clipContext.IsFirstStage(stage)
            && clipContext.Clip.SourceVideo is null
            && g.GetVideoStagesSpec().IsTextToVideo;
    }

    private StageFrame PrepareStage(
        StagePlan stage,
        int sectionId,
        ClipContext clipContext,
        StageExecutionOptions executionOptions)
    {
        JArray priorOutputPath = CopyPath(g.CurrentMedia.Path);
        ltxManager.PrepareReusableAudio(clipContext, stage);
        bool replaceTextToVideoRootStage = ReplacesTextToVideoRoot(stage, clipContext);
        LtxPostVideoChainCapture postVideoChain = replaceTextToVideoRootStage
            ? null
            : ltxManager.TryCapturePostVideoChain(clipContext, stage);
        WGNodeData sourceMedia = replaceTextToVideoRootStage
            ? CloneMedia(g.CurrentMedia)
            : ApplyStageUpscaleIfNeeded(clipContext, stage, sectionId, postVideoChain);
        if (sourceMedia is null)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: stage {stage.StageId} could not resolve its source media.");
        }
        WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(clipContext, stage, sectionId, sourceMedia, replaceTextToVideoRootStage);
        return new StageFrame(
            stage,
            sectionId,
            clipContext,
            priorOutputPath,
            replaceTextToVideoRootStage,
            postVideoChain,
            sourceMedia,
            genInfo,
            executionOptions);
    }

    private WorkflowGenerator.ImageToVideoGenInfo BuildGenInfo(
        ClipContext clipContext,
        StagePlan stage,
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
            throw new SwarmUserErrorException(
                $"VideoStages: stage {stage.StageId} could not resolve LTX video model "
                + $"'{stage.Core.Model}'.");
        }
        _ = g.NodeHelpers.Remove($"modelloader_{videoModel.Name}_image2video");

        bool sourceIsVideo = sourceMedia.DataType == WGNodeData.DT_VIDEO;
        (int batchIndex, int batchLen) = sourceIsVideo ? (0, 1) : (-1, -1);

        (string positivePrompt, string negativePrompt) = BuildClipPrompts(clip, stage);

        (int stageWidth, int stageHeight) = SnapDimsForIcLoraFactor(
            clip,
            stage,
            sourceMedia.Width ?? dimensions.Width,
            sourceMedia.Height ?? dimensions.Height);

        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = g,
            VideoModel = videoModel,
            VideoSwapModel = null,
            VideoSwapPercent = 0.5,
            Frames = ResolveFrames(sourceMedia, sectionId, replaceTextToVideoRootStage),
            VideoCFG = stage.Core.CfgScale,
            VideoFPS = spec.FPS,
            Width = stageWidth,
            Height = stageHeight,
            Prompt = positivePrompt,
            NegativePrompt = negativePrompt,
            Steps = stage.Core.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.StageId,
            BatchIndex = batchIndex,
            BatchLen = batchLen,
            ContextID = sectionId,
            VideoEndFrame = g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
        };
        return genInfo;
    }

    // IC-LoRAs with a reference-downscale factor (ref0.5 / spatial upscalers) hard-error in the
    // guide node unless pixel dims are multiples of 32×factor; the official workflows snap dims
    // the same way (a `factor*32` math node feeding "scale to multiple").
    private static (int Width, int Height) SnapDimsForIcLoraFactor(
        ClipSpec clip,
        StagePlan stage,
        int width,
        int height)
    {
        int multiple = 32 * IcLoraApplicator.MaxKnownIcLoraDownscaleFactor(
            clip, stage.ClipStageRawIndex);
        if (multiple <= 32 || (width % multiple == 0 && height % multiple == 0))
        {
            return (width, height);
        }
        int snappedWidth = Math.Max(multiple, width / multiple * multiple);
        int snappedHeight = Math.Max(multiple, height / multiple * multiple);
        Logs.Info(
            $"VideoStages: stage {stage.StageId} dims {width}x{height} snapped to "
            + $"{snappedWidth}x{snappedHeight} — the active IC-LoRA's reference downscale factor "
            + $"requires multiples of {multiple}.");
        return (snappedWidth, snappedHeight);
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

    private (string Positive, string Negative) BuildClipPrompts(ClipSpec clip, StagePlan stage)
    {
        string positive = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negative = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        string originalPositive = PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.Prompt.Type.ID, positive);
        string originalNegative = PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.NegativePrompt.Type.ID, negative);
        return (
            PromptParser.ExtractPrompt(positive, originalPositive, clip.Id, stage.StageId, stage.ClipStageIndex),
            PromptParser.ExtractPrompt(negative, originalNegative, clip.Id, stage.StageId, stage.ClipStageIndex));
    }

    private static ParamSnapshot ApplyStageLoras(T2IParamInput input, StagePlan stage)
    {
        if (stage.Loras.IsDefaultOrEmpty)
        {
            return null;
        }

        List<string> loras = [.. input.Get(T2IParamTypes.Loras) ?? []];
        List<string> weights = [.. input.Get(T2IParamTypes.LoraWeights) ?? []];
        List<string> tencWeights = [.. input.Get(T2IParamTypes.LoraTencWeights) ?? []];
        List<string> confinements = [.. input.Get(T2IParamTypes.LoraSectionConfinement) ?? []];

        List<(string, string, string)> rows = [.. stage.Loras.Select(lora => (
            lora.Name,
            LoraParams.FormatWeight(lora.ModelWeight),
            LoraParams.FormatWeight(lora.TextEncoderWeight)))];
        return LoraParams.AppendVideoScoped(input, loras, weights, tencWeights, confinements, rows);
    }

    private WGNodeData ApplyStageUpscaleIfNeeded(
        ClipContext clipContext,
        StagePlan stage,
        int sectionId,
        LtxPostVideoChainCapture postVideoChain)
    {
        ClipDimensionState dimensions = clipContext.Dimensions;
        WGNodeData source = VaeDecodePreference.AsRawImage(g, g.CurrentMedia, g.CurrentVae);
        int width = Math.Max(source.Width ?? dimensions.Width, 16);
        int height = Math.Max(source.Height ?? dimensions.Height, 16);
        source.Width = width;
        source.Height = height;

        if (stage.Upscale.Mode == StageUpscaleMode.None || string.IsNullOrWhiteSpace(stage.Upscale.RawMethod))
        {
            g.CurrentMedia = source;
            return source;
        }

        int targetWidth = Math.Max(16, (int)Math.Round(width * stage.Upscale.Factor));
        int targetHeight = Math.Max(16, (int)Math.Round(height * stage.Upscale.Factor));
        targetWidth = (targetWidth / 16) * 16;
        targetHeight = (targetHeight / 16) * 16;
        (targetWidth, targetHeight) = SnapDimsForIcLoraFactor(
            clipContext.Clip, stage, targetWidth, targetHeight);

        T2IModel stageVideoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null, sectionId: sectionId);
        bool isLtxv2Stage = VideoStageModelCompat.IsLtxV2VideoModel(stageVideoModel);
        if (isLtxv2Stage && stage.Upscale.Mode is StageUpscaleMode.LatentModel or StageUpscaleMode.Latent)
        {
            g.CurrentMedia = source;
            return source;
        }

        WGNodeData upscaleSource = ResolveUpscaleSourceMedia(source, postVideoChain, width, height);

        if (stage.Upscale.Mode == StageUpscaleMode.Pixel)
        {
            string method = stage.Upscale.MethodName;
            ImageScaleNode scaleNode = AddStagePixelScale(upscaleSource.Path, targetWidth, targetHeight, method);
            g.CurrentMedia = upscaleSource.WithPath(scaleNode.IMAGE);
            g.CurrentMedia.Width = targetWidth;
            g.CurrentMedia.Height = targetHeight;
            dimensions.Width = targetWidth;
            dimensions.Height = targetHeight;
            return g.CurrentMedia;
        }

        if (stage.Upscale.Mode == StageUpscaleMode.Model)
        {
            string modelName = stage.Upscale.MethodName;
            ImageScaleNode fitScale = AddModelUpscaleChain(upscaleSource.Path, modelName, targetWidth, targetHeight);
            g.CurrentMedia = upscaleSource.WithPath(fitScale.IMAGE);
            g.CurrentMedia.Width = targetWidth;
            g.CurrentMedia.Height = targetHeight;
            dimensions.Width = targetWidth;
            dimensions.Height = targetHeight;
            return g.CurrentMedia;
        }

        if (stage.Upscale.Mode != StageUpscaleMode.None)
        {
            Logs.Warning(
                $"VideoStages: Stage {stage.StageId} uses unsupported upscale method "
                + $"'{stage.Upscale.RawMethod}'. Ignoring upscale.");
        }

        g.CurrentMedia = source;
        return source;
    }

    /// <summary>
    /// The drive media for "Stage Input" IC-LoRA entries. Must not read the live post-video
    /// chain: its decode gets re-pointed to this stage's own sampler output, which would close a
    /// cycle through the guide (latent upscale stages keep SourceMedia attached to that chain;
    /// pixel/model upscale stages already detached it in ApplyStageUpscaleIfNeeded).
    /// </summary>
    private WGNodeData ResolveIcLoraStageInput(ClipSpec clip, StageFrame stageFrame)
    {
        WGNodeData source = stageFrame.SourceMedia;
        LtxPostVideoChainCapture postVideoChain = stageFrame.PostVideoChain;
        bool wantsStageInput = stageFrame.Stage.IcLoras.Any(entry =>
            entry.Drive.Kind is IcLoraDriveSourceKind.StageInput or IcLoraDriveSourceKind.SourcedClipInput);
        if (!wantsStageInput
            || postVideoChain is null
            || !ReferencesPostVideoChainOutput(source, postVideoChain))
        {
            return source;
        }
        return postVideoChain.CreateDetachedGuideMedia(g.CurrentVae) ?? source;
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

    private ImageScaleNode AddStagePixelScale(JArray sourcePath, int width, int height, string upscaleMethod)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        return ImageScaleReuse.RetargetOrCreate(bridge, sourcePath, width, height, "disabled", upscaleMethod);
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

    internal static JArray CopyPath(JArray path)
    {
        if (path is not { Count: 2 })
        {
            return null;
        }
        return new JArray(path[0], path[1]);
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

    private RuntimeArtifact CaptureOutput(StagePlan stage)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        RuntimeArtifact output = RuntimeArtifact.Capture(g, bridge, ArtifactOrigin.StageOutput);
        if (!output.HasMedia)
        {
            throw new SwarmUserErrorException(
                $"VideoStages: stage {stage.StageId} did not produce a video artifact.");
        }
        return output;
    }
}
