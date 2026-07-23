using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages.LTX2;

internal sealed class LtxStageLatentBuilder
{
    private readonly WorkflowGenerator g;
    private readonly LtxStageRuntimeSettings runtimeSettings;
    private readonly LtxStageLatentAudioFactory latentAudioFactory;
    private readonly LtxReusableLatentResolver reusableLatentResolver;
    private readonly LtxRetakeMaskApplicator retakeMaskApplicator;

    internal LtxStageLatentBuilder(
        WorkflowGenerator g,
        LtxStageRuntimeSettings runtimeSettings)
    {
        this.g = g;
        this.runtimeSettings = runtimeSettings;
        latentAudioFactory = new LtxStageLatentAudioFactory(g, runtimeSettings);
        reusableLatentResolver = new LtxReusableLatentResolver(g);
        retakeMaskApplicator = new LtxRetakeMaskApplicator(g);
    }

    internal WGNodeData Build(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        LtxPostVideoChainCapture postVideoChain)
    {
        StagePlan stage = stageFrame.Stage;
        ClipPlan clip = stageFrame.ClipContext.PlannedClip
            ?? throw new InvalidOperationException(
                "LTX stage execution requires the compiled clip plan.");
        genInfo.StartStep = (int)Math.Floor(stage.Core.Steps * (1 - stage.Core.Control));
        // The per-frame noise mask (not StartStep) gates what regenerates, so the whole latent must
        // stay denoise-capable from step 0.
        bool retakeActive = stage.Retake is not null
            && VideoStageModelCompat.IsLtxV2VideoModel(stage.Core.Model);
        if (retakeActive)
        {
            genInfo.StartStep = 0;
        }
        JArray controlNetLengthFrames =
            latentAudioFactory.TryResolveControlNetLengthFrames(clip.Audio.Length);

        if (stageFrame.ReplacesTextToVideoRoot)
        {
            return latentAudioFactory.CreateEmpty(
                genInfo,
                stageFrame,
                sourceMedia,
                controlNetLengthFrames);
        }

        if (postVideoChain?.CanReuseCurrentOutputAsStageInput(sourceMedia) == true)
        {
            WGNodeData nativeVideoLatent = postVideoChain.CreateStageInputVideoLatent();
            return latentAudioFactory.EnsureHasAudio(nativeVideoLatent, genInfo, sourceMedia);
        }

        if (!genInfo.Frames.HasValue && controlNetLengthFrames is null)
        {
            return null;
        }
        if (sourceMedia?.DataType == WGNodeData.DT_IMAGE)
        {
            return latentAudioFactory.CreateEmpty(
                genInfo,
                stageFrame,
                sourceMedia,
                controlNetLengthFrames);
        }

        bool matchAudioLength = controlNetLengthFrames is null
            && LtxStageLatentAudioFactory.ShouldMatchStageLengthToAudio(clip.Audio)
            && sourceMedia?.AttachedAudio?.Path is not null;

        if (reusableLatentResolver.TryResolve(
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
            reusedLatent = retakeMaskApplicator.ApplyIfActive(
                reusedLatent,
                genInfo,
                stage.Retake,
                retakeActive);
            return latentAudioFactory.EnsureHasAudio(reusedLatent, genInfo, sourceMedia);
        }

        WGNodeData sourceSnapshot = sourceMedia;
        if (postVideoChain is not null && ReferencesCurrentOutputPath(sourceMedia, postVideoChain))
        {
            sourceSnapshot = postVideoChain.CreateDetachedGuideMedia(genInfo.Vae);
        }

        bool sourceCarriesPriorAudioLatent =
            sourceMedia.AttachedAudio?.DataType == WGNodeData.DT_LATENT_AUDIO;
        JArray audioLengthFrames = matchAudioLength && !sourceCarriesPriorAudioLatent
            ? latentAudioFactory.BuildAudioLengthFrames(
                sourceMedia.AttachedAudio,
                runtimeSettings.ResolveFps(genInfo, sourceMedia)).FramesConnection
            : null;
        JArray dynamicLengthFrames = controlNetLengthFrames ?? audioLengthFrames;

        WGNodeData stageVideoInput;
        if (dynamicLengthFrames is null && IsPixelOrModelScaleStage(stage))
        {
            stageVideoInput = sourceSnapshot.WithPath(sourceSnapshot.Path);
            stageVideoInput.Frames = Math.Min(
                genInfo.Frames ?? LtxStageRuntimeSettings.DefaultFrameCount,
                stageVideoInput.Frames ?? int.MaxValue);
        }
        else
        {
            string fromBatch = AddImageFromBatch(
                sourceSnapshot.Path,
                batchIndex: 0,
                length: FrameCountToken(
                    dynamicLengthFrames,
                    genInfo.Frames ?? LtxStageRuntimeSettings.DefaultFrameCount));
            stageVideoInput = sourceSnapshot.WithPath([fromBatch, 0]);
            stageVideoInput.Frames = dynamicLengthFrames is null
                ? Math.Min(
                    genInfo.Frames ?? LtxStageRuntimeSettings.DefaultFrameCount,
                    stageVideoInput.Frames ?? int.MaxValue)
                : null;
        }
        WGNodeData encodedLatent = stageVideoInput.AsLatentImage(genInfo.Vae);
        encodedLatent = retakeMaskApplicator.ApplyIfActive(
            encodedLatent,
            genInfo,
            stage.Retake,
            retakeActive);
        return latentAudioFactory.EnsureHasAudio(encodedLatent, genInfo, sourceMedia);
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

    private static bool IsPixelOrModelScaleStage(StagePlan stage) =>
        stage.Upscale.Mode is StageUpscaleMode.Pixel or StageUpscaleMode.Model;

    private static JToken FrameCountToken(JArray framesConnection, int fallbackFrames) =>
        framesConnection is null
            ? new JValue(fallbackFrames)
            : LtxFrameCountConnector.CloneConnection(framesConnection);

    private static bool ReferencesCurrentOutputPath(
        WGNodeData media,
        LtxPostVideoChainCapture postVideoChain)
    {
        if (media?.Path is not JArray mediaPath || postVideoChain is null)
        {
            return false;
        }

        return JToken.DeepEquals(mediaPath, postVideoChain.CurrentOutputMedia?.Path)
            || JToken.DeepEquals(mediaPath, postVideoChain.DecodeOutputPath);
    }
}
