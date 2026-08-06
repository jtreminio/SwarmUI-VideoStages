using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxStageLatentBuilder
{
    private readonly WorkflowGenerator g;
    private readonly LtxStageLatentAudioFactory latentAudioFactory;
    private readonly LtxReusableLatentResolver reusableLatentResolver;
    private readonly LtxVideoRetakeMasker retakeMasker;

    internal LtxStageLatentBuilder(WorkflowGenerator g)
    {
        this.g = g;
        latentAudioFactory = new LtxStageLatentAudioFactory(g);
        reusableLatentResolver = new LtxReusableLatentResolver(g);
        retakeMasker = new LtxVideoRetakeMasker(g);
    }

    internal WGNodeData Build(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        LtxPostVideoChainCapture postVideoChain)
    {
        StagePlan stage = stageFrame.Stage;
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        ClipPlan clip = stageFrame.ClipContext.PlannedClip
            ?? throw Invariant.Failure(
                "LTX stage execution requires the compiled clip plan.");
        genInfo.StartStep = StageStartStepPolicy.StartStep(
            payload.Core.Steps,
            payload.Core.Control);
        bool retakeActive = stage.HasActiveRetakeMask();
        if (retakeActive)
        {
            // The noise mask, not StartStep, limits regeneration during retakes.
            genInfo.StartStep = 0;
        }
        JArray controlNetLengthFrames =
            latentAudioFactory.TryResolveControlNetLengthFrames(clip);

        if (stageFrame.ReplacesTextToVideoRoot
            || (stageFrame.ClipContext.IncomingContinueHandleFrames > 0
                && !stageFrame.ClipContext.ContinueHandleMaterialized))
        {
            WGNodeData empty = latentAudioFactory.CreateEmpty(
                genInfo,
                stageFrame,
                sourceMedia,
                controlNetLengthFrames,
                stageFrame.Claim.Latent);
            if (stageFrame.ClipContext.IncomingContinueHandleFrames > 0)
            {
                stageFrame.ClipContext.ContinueHandleMaterialized = true;
            }
            return empty;
        }

        if (postVideoChain?.CanReuseCurrentOutputAsStageInput(sourceMedia) == true)
        {
            WGNodeData nativeVideoLatent = postVideoChain.CreateStageInputVideoLatent();
            nativeVideoLatent = retakeMasker.ApplyIfActive(
                nativeVideoLatent,
                genInfo,
                payload.Retake,
                retakeActive);
            return latentAudioFactory.EnsureHasAudio(
                nativeVideoLatent,
                genInfo,
                stageFrame,
                sourceMedia);
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
            reusedLatent = retakeMasker.ApplyIfActive(
                reusedLatent,
                genInfo,
                payload.Retake,
                retakeActive);
            return latentAudioFactory.EnsureHasAudio(
                reusedLatent,
                genInfo,
                stageFrame,
                sourceMedia);
        }

        WGNodeData sourceSnapshot = sourceMedia;
        if (postVideoChain?.ReferencesOutput(sourceMedia) == true)
        {
            sourceSnapshot = postVideoChain.CreateDetachedGuideMedia(genInfo.Vae);
        }

        bool sourceCarriesPriorAudioLatent =
            sourceMedia.AttachedAudio?.DataType == WGNodeData.DT_LATENT_AUDIO;
        JArray audioLengthFrames = matchAudioLength && !sourceCarriesPriorAudioLatent
            ? latentAudioFactory.BuildAudioLengthFrames(
                sourceMedia.AttachedAudio,
                LtxStageRuntimeSettings.ResolveFps(g, genInfo, sourceMedia)).FramesConnection
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
        encodedLatent = retakeMasker.ApplyIfActive(
            encodedLatent,
            genInfo,
            payload.Retake,
            retakeActive);
        return latentAudioFactory.EnsureHasAudio(
            encodedLatent,
            genInfo,
            stageFrame,
            sourceMedia);
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
        stage.Core.Upscale.Mode
            is StageUpscaleMode.Pixel or StageUpscaleMode.Model;

    private static JToken FrameCountToken(JArray framesConnection, int fallbackFrames) =>
        framesConnection is null
            ? new JValue(fallbackFrames)
            : framesConnection?.DeepClone() as JArray;
}
