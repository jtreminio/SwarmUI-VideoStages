using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages.LTX2;

internal sealed record ResolvedClipRef(WGNodeData Image, ImageRefSpec Spec, double Strength);

internal sealed class LtxStageExecutor(
    WorkflowGenerator g,
    RootVideoStageResizer rootVideoStageResizer)
{
    private const double PromptRelayEpsilon = 0.001;
    private const double DefaultGuideMergeStrength = 1.0;
    private const int DefaultFps = 24;
    private const int DefaultFrameCount = 97;
    private const double DefaultCfg = 3;
    private const string DefaultSampler = "euler";
    private const string DefaultScheduler = "normal";

    internal const int DefaultFpsValue = DefaultFps;
    internal const int DefaultFrameCountValue = DefaultFrameCount;
    internal const double DefaultCfgValue = DefaultCfg;
    internal const string DefaultSamplerValue = DefaultSampler;
    internal const string DefaultSchedulerValue = DefaultScheduler;

    public void RunStage(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        bool skipGuideReinjection,
        LtxPostVideoChainCapture postVideoChain,
        IReadOnlyList<ResolvedClipRef> clipRefs = null,
        double guideMergeStrength = DefaultGuideMergeStrength)
    {
        postVideoChain?.AttachSourceAudio(sourceMedia);
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
            PrepareModelAndConditioning(genInfo, stageFrame, effectiveSourceMedia);
            PrepareConditioning(
                genInfo,
                stageFrame,
                effectiveSourceMedia,
                guideMedia,
                skipGuideReinjection,
                postVideoChain,
                clipRefs ?? [],
                guideMergeStrength);
            genInfo.VideoCFG ??= genInfo.DefaultCFG;

            foreach (
                Action<WorkflowGenerator.ImageToVideoGenInfo> handler in
                WorkflowGenerator.AltImageToVideoPostHandlers)
            {
                handler(genInfo);
            }

            ExecuteSampler(genInfo, stageFrame);
            FinalizeOutput(genInfo, stageFrame, effectiveSourceMedia, postVideoChain);
        }
        finally
        {
            g.IsImageToVideo = false;
        }
    }

    private void PrepareModelAndConditioning(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia)
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

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput clipOutput = bridge.ResolvePath(clip.Path);

        SwarmClipTextEncodeAdvancedNode negCondNode = AddSwarmClipTextEncodeAdvanced(
            bridge, clipOutput, steps, negativePrompt, width, height, guidance);
        genInfo.NegCond = negCondNode.CONDITIONING.ToPath();

        if (TryBuildPromptRelay(
                bridge, genInfo, stageFrame, clipOutput, positivePrompt, sourceMedia,
                out string overridePositive))
        {
            return;
        }

        SwarmClipTextEncodeAdvancedNode posCondNode = AddSwarmClipTextEncodeAdvanced(
            bridge, clipOutput, steps, overridePositive ?? positivePrompt, width, height, guidance);
        genInfo.PosCond = posCondNode.CONDITIONING.ToPath();
    }

    private bool TryBuildPromptRelay(
        WorkflowBridge bridge,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        INodeOutput clipOutput,
        string globalPrompt,
        WGNodeData sourceMedia,
        out string overridePositive)
    {
        overridePositive = null;
        if (stageFrame?.ClipContext?.Clip?.PromptWindows is not { Count: > 0 } windows
            || genInfo.Model?.Path is not JArray modelPath)
        {
            return false;
        }

        int fps = ResolveFps(genInfo, sourceMedia);
        int frameCount = genInfo.Frames ?? sourceMedia?.Frames ?? DefaultFrameCount;
        double clipSeconds = frameCount / (double)Math.Max(1, fps);

        List<(string Prompt, double Seconds)> tiled = PromptWindowTiler.TilePromptWindows(windows, clipSeconds);
        if (tiled.Count == 1 && !string.IsNullOrWhiteSpace(tiled[0].Prompt))
        {
            overridePositive = tiled[0].Prompt;
            return false;
        }
        if (tiled.Count < 2)
        {
            return false;
        }

        JArray windowsJson = new(tiled.Select(window => new JObject
        {
            ["prompt"] = window.Prompt ?? "",
            ["seconds"] = Math.Round(window.Seconds, 1),
        }));

        SwarmPromptRelayEncodeNode relay = bridge.AddNode(new SwarmPromptRelayEncodeNode().With(
            GlobalPrompt: globalPrompt ?? "",
            Windows: windowsJson.ToString(Newtonsoft.Json.Formatting.None),
            Fps: fps,
            Epsilon: PromptRelayEpsilon));
        relay.ModelInput.ConnectFromPath(bridge, modelPath);
        relay.Clip.TryConnectToUntyped(clipOutput);
        bridge.SyncNode(relay);

        genInfo.Model = genInfo.Model.WithPath(relay.Model);
        genInfo.PosCond = relay.Positive.ToPath();
        return true;
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
        SwarmClipTextEncodeAdvancedNode node = bridge.AddNode(new SwarmClipTextEncodeAdvancedNode().With(
            Steps: steps,
            Prompt: prompt ?? "",
            Width: width,
            Height: height,
            TargetWidth: width,
            TargetHeight: height,
            Guidance: guidance));
        node.Clip.TryConnectToUntyped(clipOutput);
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
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        WGNodeData guideMedia,
        bool skipGuideReinjection,
        LtxPostVideoChainCapture postVideoChain,
        IReadOnlyList<ResolvedClipRef> clipRefs,
        double guideMergeStrength)
    {
        WGNodeData stageLatent = BuildStageLatent(genInfo, stageFrame, sourceMedia, postVideoChain);
        if (stageLatent is null)
        {
            genInfo.PrepFullCond(g, guideMedia);
            return;
        }

        new LtxConditioningPipeline(g, genInfo, stageFrame, this)
            .WithLatent(stageLatent, sourceMedia)
            .WithUpscaleIfNeeded(sourceMedia)
            .WithInplaceMerges(clipRefs)
            .BindToCurrentMedia(skipGuideReinjection, guideMedia, guideMergeStrength)
            .WithLtxvConditioning()
            .WithGuideAdditions(clipRefs);
    }

    internal string CreateLtxvImgToVideoInplaceNode(
        JToken vaePath,
        JArray preprocessedImagePath,
        JArray latentPath,
        double strength,
        bool bypass)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        LTXVImgToVideoInplaceNode node = bridge.AddNode(new LTXVImgToVideoInplaceNode().With(
            Strength: strength,
            Bypass: bypass));
        if (vaePath is JArray vaeArr)
        {
            node.Vae.ConnectFromPath(bridge, vaeArr);
        }
        node.Image.ConnectFromPath(bridge, preprocessedImagePath);
        node.LatentInput.ConnectFromPath(bridge, latentPath);
        return node.Id;
    }

    private WGNodeData BuildStageLatent(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        LtxPostVideoChainCapture postVideoChain)
    {
        StagePlan stage = stageFrame.Stage;
        ClipSpec clip = stageFrame.ClipContext.Clip;
        genInfo.StartStep = (int)Math.Floor(stage.Core.Steps * (1 - stage.Core.Control));
        // The per-frame noise mask (not StartStep) gates what regenerates, so the whole latent must
        // stay denoise-capable from step 0.
        bool retakeActive = stage.Retake is not null
            && VideoStageModelCompat.IsLtxV2VideoModel(stage.Core.Model);
        if (retakeActive)
        {
            genInfo.StartStep = 0;
        }
        JArray controlNetLengthFrames = TryResolveControlNetLengthFrames(clip);

        if (stageFrame.ReplacesTextToVideoRoot)
        {
            return CreateEmptyVideoLatent(genInfo, stageFrame, sourceMedia, controlNetLengthFrames);
        }

        if (postVideoChain?.CanReuseCurrentOutputAsStageInput(sourceMedia) == true)
        {
            WGNodeData nativeVideoLatent = postVideoChain.CreateStageInputVideoLatent();
            return EnsureHasAudioWithLtxFps(nativeVideoLatent, genInfo, sourceMedia);
        }

        if (!genInfo.Frames.HasValue && controlNetLengthFrames is null)
        {
            return null;
        }
        if (sourceMedia?.DataType == WGNodeData.DT_IMAGE)
        {
            (int width, int height) = ResolveStageLatentDims(stageFrame, sourceMedia);
            return CreateEmptyVideoLatentWithOptionalAudioLength(
                clip,
                genInfo,
                sourceMedia,
                width,
                height,
                genInfo.Frames ?? sourceMedia.Frames ?? DefaultFrameCount,
                sourceMedia.AttachedAudio,
                controlNetLengthFrames);
        }

        bool matchAudioLength = controlNetLengthFrames is null
            && ShouldMatchStageLengthToAudio(clip)
            && sourceMedia?.AttachedAudio?.Path is not null;

        if (TryGetReusableDecodedVideoLatent(
                sourceMedia,
                genInfo,
                allowDynamicFrameCount: controlNetLengthFrames is not null || matchAudioLength,
                out JArray reusableLatentPath))
        {
            WGNodeData reusedLatent = sourceMedia.WithPath(
                reusableLatentPath,
                WGNodeData.DT_LATENT_VIDEO,
                genInfo.Vae.Compat);
            reusedLatent.Frames = matchAudioLength || !genInfo.Frames.HasValue
                ? null
                : Math.Min(genInfo.Frames.Value, reusedLatent.Frames ?? int.MaxValue);
            reusedLatent = ApplyRetakeMaskIfActive(reusedLatent, genInfo, stage, retakeActive);
            return EnsureHasAudioWithLtxFps(reusedLatent, genInfo, sourceMedia);
        }

        WGNodeData sourceSnapshot = sourceMedia;
        if (postVideoChain is not null && ReferencesCurrentOutputPath(sourceMedia, postVideoChain))
        {
            sourceSnapshot = postVideoChain.CreateDetachedGuideMedia(genInfo.Vae);
        }

        bool sourceCarriesPriorAudioLatent = sourceMedia.AttachedAudio?.DataType == WGNodeData.DT_LATENT_AUDIO;
        JArray audioLengthFrames = matchAudioLength && !sourceCarriesPriorAudioLatent
            ? BuildAudioLengthFramesNode(sourceMedia.AttachedAudio, ResolveFps(genInfo, sourceMedia)).FramesConnection
            : null;
        JArray dynamicLengthFrames = controlNetLengthFrames ?? audioLengthFrames;

        WGNodeData stageVideoInput;
        if (dynamicLengthFrames is null && IsPixelOrModelScaleStage(stageFrame.Stage))
        {
            stageVideoInput = sourceSnapshot.WithPath(sourceSnapshot.Path);
            stageVideoInput.Frames = Math.Min(genInfo.Frames ?? DefaultFrameCount, stageVideoInput.Frames ?? int.MaxValue);
        }
        else
        {
            string fromBatch = AddImageFromBatch(
                sourceSnapshot.Path,
                batchIndex: 0,
                length: FrameCountToken(dynamicLengthFrames, genInfo.Frames ?? DefaultFrameCount));
            stageVideoInput = sourceSnapshot.WithPath([fromBatch, 0]);
            stageVideoInput.Frames = dynamicLengthFrames is null
                ? Math.Min(genInfo.Frames ?? DefaultFrameCount, stageVideoInput.Frames ?? int.MaxValue)
                : null;
        }
        WGNodeData encodedLatent = stageVideoInput.AsLatentImage(genInfo.Vae);
        encodedLatent = ApplyRetakeMaskIfActive(encodedLatent, genInfo, stage, retakeActive);
        return EnsureHasAudioWithLtxFps(encodedLatent, genInfo, sourceMedia);
    }

    private WGNodeData ApplyRetakeMaskIfActive(
        WGNodeData encodedLatent,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StagePlan stage,
        bool retakeActive)
    {
        if (!retakeActive || stage.Retake is null)
        {
            return encodedLatent;
        }
        return new LtxVideoRetakeMasker(g).Apply(
            encodedLatent,
            genInfo,
            new RetakeWindowSpec(stage.Retake.StartFrame, stage.Retake.LengthFrames, stage.Retake.Strength));
    }

    private static bool IsPixelOrModelScaleStage(StagePlan stage)
    {
        return stage.Upscale.Mode is StageUpscaleMode.Pixel or StageUpscaleMode.Model;
    }

    private string AddImageFromBatch(JArray imagePath, int batchIndex, JToken length)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        ImageFromBatchNode node = bridge.AddNode(new ImageFromBatchNode().With(
            BatchIndex: batchIndex));
        node.Image.TryConnectFromPath(bridge, imagePath);
        node.Length.SetFromToken(bridge, length);
        return node.Id;
    }

    private WGNodeData CreateEmptyVideoLatent(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        JArray controlNetLengthFrames = null)
    {
        (int width, int height) = ResolveStageLatentDims(stageFrame, sourceMedia);
        int frames = genInfo.Frames ?? sourceMedia?.Frames ?? DefaultFrameCount;
        WGNodeData attachedAudio = sourceMedia?.AttachedAudio;
        return CreateEmptyVideoLatentWithOptionalAudioLength(
            stageFrame.ClipContext.Clip,
            genInfo,
            sourceMedia,
            width,
            height,
            frames,
            attachedAudio,
            controlNetLengthFrames);
    }

    /// <summary>
    /// The clip's configured timeline resolution wins over the incoming media's size: e.g. with a
    /// sourced FIRST clip, the footage conforms to the spec dims while the kept-alive root generation
    /// stays at the core params' — sizing later clips from that root media would splinter the timeline
    /// across resolutions (and degrade every overlap boundary merge to a hard cut).
    /// </summary>
    private (int Width, int Height) ResolveStageLatentDims(StageFrame stageFrame, WGNodeData sourceMedia)
    {
        ClipDimensionState dims = stageFrame.ClipContext.Dimensions;
        int width = dims.Width > 0 ? dims.Width : sourceMedia?.Width ?? g.UserInput.GetImageWidth();
        int height = dims.Height > 0 ? dims.Height : sourceMedia?.Height ?? g.UserInput.GetImageHeight();
        return (Math.Max(width, 16), Math.Max(height, 16));
    }

    private (JArray FramesConnection, WGNodeData EffectiveAudio) BuildAudioLengthFramesNode(
        WGNodeData attachedAudio,
        int fps)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        JToken lengthFramesAudioSource = LtxAudioPathResolution.ResolveLengthToFramesAudioSource(
            bridge,
            attachedAudio.Path,
            null);

        SwarmAudioLengthToFramesNode lengthToFrames = bridge.AddNode(new SwarmAudioLengthToFramesNode()).With(
            FrameRate: fps);
        if (lengthFramesAudioSource is JArray audioSourceArr)
        {
            lengthToFrames.AudioInput.TryConnectFromPath(bridge, audioSourceArr);
        }

        WGNodeData effectiveAudio = new(
            WorkflowBridge.ToPath(lengthToFrames.Audio),
            g,
            WGNodeData.DT_AUDIO,
            g.CurrentAudioVae?.Compat ?? attachedAudio.Compat);
        return (WorkflowBridge.ToPath(lengthToFrames.Frames), effectiveAudio);
    }

    private WGNodeData CreateEmptyVideoLatentWithOptionalAudioLength(
        ClipSpec clip,
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

        if (controlNetLengthFrames is null
            && ShouldMatchStageLengthToAudio(clip)
            && effectiveAttached?.Path is not null
            && effectiveAttached.DataType != WGNodeData.DT_LATENT_AUDIO)
        {
            (audioLengthFrames, effectiveAttached) = BuildAudioLengthFramesNode(effectiveAttached, fps);
        }

        using WorkflowBridge bridge = BridgeSync.For(g);

        JArray dynamicLengthFrames = controlNetLengthFrames ?? audioLengthFrames;
        JToken latentLength = dynamicLengthFrames is null
            ? new JValue(frames)
            : LtxFrameCountConnector.CloneConnection(dynamicLengthFrames);

        EmptyLTXVLatentVideoNode emptyNode = bridge.AddNode(new EmptyLTXVLatentVideoNode());
        emptyNode.With(
            Width: width,
            Height: height,
            BatchSize: 1);
        emptyNode.Length.SetFromToken(bridge, latentLength);

        WGNodeData stageLatent = new(emptyNode.LATENT.ToPath(), g, WGNodeData.DT_LATENT_VIDEO, genInfo.Model.Compat)
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
        if (framesConnection is JArray framesArr)
        {
            emptyAudio.FramesNumber.TryConnectFromPath(bridge, framesArr);
        }
        emptyAudio.FrameRate.Set(frameRate);
        bridge.SyncNode(emptyAudio);
    }

    private JArray TryResolveControlNetLengthFrames(ClipSpec clip)
    {
        if (!clip.ClipLengthFromControlNet)
        {
            return null;
        }
        return new ControlNetCapture(g).TryCreateCapturedControlImageFrameCount(
            clip.PrimarySlotEntry?.Source,
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
            || sourceMedia.Path is null
            || genInfo?.Vae?.Path is null
            || (!genInfo.Frames.HasValue && !allowDynamicFrameCount)
            || genInfo.Frames.HasValue
                && sourceMedia.Frames is int sourceFrames
                && sourceFrames > genInfo.Frames.Value)
        {
            return false;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        (INodeOutput samples, INodeOutput decodeVae) =
            bridge.ResolvePath(sourceMedia.Path)?.Node is IVaeDecode decode
                ? (decode.Samples.Connection, decode.Vae.Connection)
                : (null, null);
        if (samples is null || decodeVae is null)
        {
            return false;
        }

        INodeOutput vaeOutput = bridge.ResolvePath(genInfo.Vae.Path);
        bool sameVaeNode = vaeOutput is not null
            && decodeVae.Node == vaeOutput.Node
            && decodeVae.SlotIndex == vaeOutput.SlotIndex;
        bool sameDynamicLtxCompat =
            allowDynamicFrameCount
            && !string.IsNullOrWhiteSpace(sourceMedia.Compat?.ID)
            && sourceMedia.Compat.ID == genInfo.Vae.Compat?.ID;
        if (!sameVaeNode && !sameDynamicLtxCompat)
        {
            return false;
        }

        latentPath = WorkflowBridge.ToPath(samples);
        return true;
    }

    private static bool ShouldMatchStageLengthToAudio(ClipSpec clip)
    {
        if (!clip.ClipLengthFromAudio)
        {
            return false;
        }
        return ClipAudioWorkflowHelper.IsExternalClipAudioSource(clip.AudioSource);
    }

    internal void ApplyResolvedFpsToWorkflow(WorkflowGenerator.ImageToVideoGenInfo genInfo, int fps)
    {
        if (fps <= 0)
        {
            return;
        }
        genInfo.VideoFPS = fps;
        g.UserInput.Set(T2IParamTypes.VideoFPS, fps, genInfo.ContextID);
    }

    internal int ResolveFps(WorkflowGenerator.ImageToVideoGenInfo genInfo, WGNodeData sourceMedia)
    {
        int? fps = genInfo.VideoFPS ?? sourceMedia.GetRawFPS();
        if (fps.HasValue && fps.Value > 0)
        {
            return fps.Value;
        }
        int specFps = g.GetVideoStagesSpec().FPS;
        return specFps > 0 ? specFps : DefaultFps;
    }

    private static bool ReferencesCurrentOutputPath(WGNodeData media, LtxPostVideoChainCapture postVideoChain)
    {
        if (media?.Path is not JArray mediaPath || postVideoChain is null)
        {
            return false;
        }

        return JToken.DeepEquals(mediaPath, postVideoChain.CurrentOutputMedia?.Path)
            || JToken.DeepEquals(mediaPath, postVideoChain.DecodeOutputPath);
    }

    internal JArray ResolvePreprocessedGuidePath(JArray guideImagePath, WGNodeData targetMedia) =>
        new LtxGuidePreprocessReuse(g, rootVideoStageResizer)
            .ResolvePreprocessedGuidePath(guideImagePath, targetMedia);

    private void ExecuteSampler(WorkflowGenerator.ImageToVideoGenInfo genInfo, StageFrame stageFrame)
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
        LtxAudioMaskResizer.ApplyCurrentAudioMaskDimensions(g.CurrentMedia);
        // Windows BOTH av channels to the retake span (preserved frames + their audio stay locked, the
        // window regenerates). No-op for plain clips with no retake window.
        new LtxAudioWindowMasker(g).Apply(genInfo, stageFrame);
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

        if (stageFrame.NeedsCropGuidesAfterSampler)
        {
            CropGuidesAfterSampler(genInfo, stageFrame);
        }

        if (genInfo.DoFirstFrameLatentSwap is not null)
        {
            ApplyFirstFrameLatentSwap(genInfo);
        }
    }

    private void ApplyFirstFrameLatentSwap(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        ReplaceVideoLatentFramesNode replace = bridge.AddNode(new ReplaceVideoLatentFramesNode().With(
            Index: 0));
        if (g.CurrentMedia?.Path is JArray destPath)
        {
            replace.Destination.ConnectFromPath(bridge, destPath);
        }
        if (genInfo.DoFirstFrameLatentSwap is JArray sourcePath)
        {
            replace.Source.TryConnectFromPath(bridge, sourcePath);
        }

        NormalizeVideoLatentStartNode normalize = bridge.AddNode(new NormalizeVideoLatentStartNode().With(
            StartFrameCount: 4,
            ReferenceFrameCount: 5,
            LatentInput: replace.LATENT));

        g.CurrentMedia = g.CurrentMedia.WithPath(normalize.Latent);
    }

    private void CropGuidesAfterSampler(WorkflowGenerator.ImageToVideoGenInfo genInfo, StageFrame stageFrame)
    {
        bool shouldRestoreAudioVideoLatent = g.CurrentMedia.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO;

        // Voice-ref clips: the crop branches from the PRE-ref-token conditioning — in the official
        // LipDub graph the ref-token wrap feeds only the sampler's guider and never flows into
        // LTXVCropGuides. (This also bypasses a retake window-mask wrap, an unsupported combo.)
        if (stageFrame.VoiceRefActive && stageFrame.VoiceRefPreWrapPosCond is not null)
        {
            genInfo.PosCond = stageFrame.VoiceRefPreWrapPosCond;
            genInfo.NegCond = stageFrame.VoiceRefPreWrapNegCond;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput cropLatentSource;
        INodeOutput audioLatentSource = null;
        if (shouldRestoreAudioVideoLatent)
        {
            LTXVSeparateAVLatentNode separate = bridge.AddNode(new LTXVSeparateAVLatentNode());
            if (g.CurrentMedia?.Path is JArray avPath)
            {
                separate.AvLatent.ConnectFromPath(bridge, avPath);
            }
            cropLatentSource = separate.VideoLatent;
            audioLatentSource = separate.AudioLatent;
        }
        else
        {
            cropLatentSource = g.CurrentMedia?.Path is JArray latentPath ? bridge.ResolvePath(latentPath) : null;
        }

        LTXVCropGuidesNode crop = bridge.AddNode(new LTXVCropGuidesNode());
        crop.ConnectConditioning(bridge, genInfo);
        crop.LatentInput.ConnectToUntyped(cropLatentSource);

        genInfo.SetConditioning(crop);

        if (shouldRestoreAudioVideoLatent)
        {
            INodeOutput concatAudioSource = audioLatentSource;
            if (stageFrame.VoiceRefActive && audioLatentSource is not null)
            {
                // Official LipDub refine carry: crop → SetAudioRefTokens(this stage's generated
                // audio) → the next stage's conditioning, with the concat taking the FROZEN audio so
                // refinement preserves the generated speech instead of renoising it. The stash lets
                // the next stage's own wrap reuse this audio as its speaker context.
                LTXVSetAudioRefTokensNode refTokens = bridge.AddNode(new LTXVSetAudioRefTokensNode());
                refTokens.PositiveInput.ConnectToUntyped(crop.Positive);
                refTokens.NegativeInput.ConnectToUntyped(crop.Negative);
                refTokens.AudioLatent.ConnectToUntyped(audioLatentSource);
                bridge.SyncNode(refTokens);
                genInfo.PosCond = WorkflowBridge.ToPath(refTokens.Positive);
                genInfo.NegCond = WorkflowBridge.ToPath(refTokens.Negative);
                g.NodeHelpers[
                    $"{VoiceRefApplicator.VoiceRefStageAudioKeyPrefix}{stageFrame.ClipContext.Clip.Id}"] =
                    WorkflowBridge.ToPath(audioLatentSource).ToString(Newtonsoft.Json.Formatting.None);
                concatAudioSource = refTokens.FrozenAudio;
            }

            LTXVConcatAVLatentNode concat = bridge.AddNode(new LTXVConcatAVLatentNode().With(
                VideoLatent: crop.Latent));
            concat.AudioLatent.TryConnectToUntyped(concatAudioSource);

            g.CurrentMedia = g.CurrentMedia.WithPath(
                concat.Latent,
                WGNodeData.DT_LATENT_AUDIOVIDEO,
                genInfo.Model.Compat);
            return;
        }

        g.CurrentMedia = g.CurrentMedia.WithPath(crop.Latent, null, genInfo.Model.Compat);
    }

    private void FinalizeOutput(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        LtxPostVideoChainCapture postVideoChain)
    {
        int outputWidth = g.CurrentMedia?.Width ?? sourceMedia.Width ?? g.UserInput.GetImageWidth();
        int outputHeight = g.CurrentMedia?.Height ?? sourceMedia.Height ?? g.UserInput.GetImageHeight();
        bool splicedIntoNativeChain = postVideoChain is not null;
        bool parallelMultiClip = stageFrame.ExecutionOptions.RequiresDedicatedOutput;
        if (splicedIntoNativeChain)
        {
            if (parallelMultiClip)
            {
                LtxPostVideoChainSplicer.SpliceCurrentOutputToDedicatedBranch(
                    postVideoChain,
                    g,
                    genInfo.Vae,
                    outputWidth,
                    outputHeight,
                    genInfo.Frames,
                    genInfo.VideoFPS);
            }
            else
            {
                LtxPostVideoChainSplicer.SpliceCurrentOutput(postVideoChain, g, genInfo.Vae);
            }

            if (postVideoChain.HasPostDecodeWrappers)
            {
                ApplyCurrentMediaOutputMetadata(
                    outputWidth,
                    outputHeight,
                    postVideoChain.CurrentOutputMedia.Frames,
                    postVideoChain.CurrentOutputMedia.GetRawFPS());
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
            ApplyCurrentMediaOutputMetadata(outputWidth, outputHeight, genInfo.Frames, genInfo.VideoFPS);
        }

        g.CurrentVae = genInfo.Vae;
    }

    private string AddSwarmTrimFrames(JArray imagePath, int trimStart, int trimEnd)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        SwarmTrimFramesNode node = bridge.AddNode(new SwarmTrimFramesNode().With(
            TrimStart: trimStart,
            TrimEnd: trimEnd));
        node.Image.TryConnectFromPath(bridge, imagePath);
        return node.Id;
    }

    private void AttachDecodedLtxAudioFromCurrentVideo()
    {
        if (g.CurrentMedia?.Path is not JArray { Count: 2 } currentPath)
        {
            return;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
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

        // MediaRef.FromWGNodeData pre-copies any existing AttachedAudio (possibly a latent
        // route) into the snapshot, so non-null cannot mean "freshly decoded" — only a
        // DT_AUDIO attachment proves AttachDecodedLtxAudio succeeded.
        if (currentMedia.AttachedAudio is MediaRef attachedAudio
            && attachedAudio.DataType == WGNodeData.DT_AUDIO)
        {
            g.CurrentMedia.AttachedAudio = attachedAudio.ToWGNodeData(g);
        }
        else if (g.CurrentMedia.AttachedAudio is WGNodeData latentAudio
            && latentAudio.DataType == WGNodeData.DT_LATENT_AUDIO
            && latentAudio.Path is JArray latentAudioPath)
        {
            // AsRawImage on a concat AV latent (e.g. the crop-guides output) reads the concat's
            // pre-join routes, so the video decode consumes the crop node — not a separate — and
            // the decode→separate shortcut above finds no audio. The stashed latent-audio route
            // is authoritative: decode it directly so no consumer sees a latent as save audio.
            LTXVAudioVAEDecodeNode audioDecode = bridge.AddNode(new LTXVAudioVAEDecodeNode());
            audioDecode.Samples.TryConnectFromPath(bridge, latentAudioPath);
            audioDecode.AudioVae.ConnectFrom(audioVae);
            bridge.SyncNode(audioDecode);
            g.CurrentMedia.AttachedAudio = new WGNodeData(
                audioDecode.Audio.ToPath(), g, WGNodeData.DT_AUDIO, audioVae.Compat);
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
            DataType = WGNodeData.DT_AUDIO,
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
