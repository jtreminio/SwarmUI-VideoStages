using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Audio;
using VideoStages.Generated;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Architectures.Ltx2.Runtime.Audio;
using VideoStages.Architectures.Ltx2.Runtime.Chain;

namespace VideoStages.Architectures.Ltx2.Runtime.Stage;

internal sealed class LtxStageLatentBuilder
{
    private readonly WorkflowGenerator g;
    private readonly LtxReusableLatentResolver reusableLatentResolver;
    private readonly LtxVideoRetakeMasker retakeMasker;

    internal LtxStageLatentBuilder(WorkflowGenerator g)
    {
        this.g = g;
        reusableLatentResolver = new LtxReusableLatentResolver(g);
        retakeMasker = new LtxVideoRetakeMasker(g);
    }

    internal WGNodeData Build(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageContext stageContext,
        WGNodeData sourceMedia,
        LtxPostVideoChain postVideoChain)
    {
        StagePlan stage = stageContext.Stage;
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        ClipPlan clip = stageContext.ClipContext.PlannedClip
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
            new LtxControlNetMediaNormalizer(g).CreateClipLengthFrames(clip, out _);

        if (stageContext.ClaimsTextToVideoRoot
            || (stageContext.ClipContext.IncomingContinueHandleFrames > 0
                && !stageContext.ClipContext.ContinueHandleMaterialized))
        {
            WGNodeData empty = CreateEmpty(
                genInfo,
                stageContext,
                sourceMedia,
                controlNetLengthFrames,
                stageContext.Claim.Latent);
            if (stageContext.ClipContext.IncomingContinueHandleFrames > 0)
            {
                stageContext.ClipContext.ContinueHandleMaterialized = true;
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
            return EnsureHasAudio(
                nativeVideoLatent,
                genInfo,
                stageContext,
                sourceMedia);
        }

        if (!genInfo.Frames.HasValue && controlNetLengthFrames is null)
        {
            return null;
        }
        if (sourceMedia?.DataType == WGNodeData.DT_IMAGE)
        {
            return CreateEmpty(
                genInfo,
                stageContext,
                sourceMedia,
                controlNetLengthFrames);
        }

        bool matchAudioLength = controlNetLengthFrames is null
            && ShouldMatchStageLengthToAudio(clip.Audio)
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
            return EnsureHasAudio(
                reusedLatent,
                genInfo,
                stageContext,
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
            ? BuildAudioLengthFrames(
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
        return EnsureHasAudio(
            encodedLatent,
            genInfo,
            stageContext,
            sourceMedia);
    }

    /// <summary>
    /// A latent generated from nothing, sized by the plan and carrying whatever audio the clip has
    /// reached this stage with.
    /// </summary>
    private WGNodeData CreateEmpty(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageContext stageContext,
        WGNodeData sourceMedia,
        JArray controlNetLengthFrames,
        string latentNodeId = null)
    {
        (int width, int height) = ResolveStageLatentDims(stageContext, sourceMedia);
        int frames = genInfo.Frames
            ?? sourceMedia?.Frames
            ?? LtxStageRuntimeSettings.DefaultFrameCount;
        WGNodeData effectiveAttached = ApplyPendingAudioConditioning(
            stageContext.ClipContext,
            sourceMedia?.AttachedAudio,
            out _);
        int fps = LtxStageRuntimeSettings.ResolveFps(g, genInfo, sourceMedia);
        JArray audioLengthFrames = null;

        if (controlNetLengthFrames is null
            && ShouldMatchStageLengthToAudio(stageContext.ClipContext.PlannedClip.Audio)
            && effectiveAttached?.Path is not null
            && effectiveAttached.DataType != WGNodeData.DT_LATENT_AUDIO)
        {
            (audioLengthFrames, effectiveAttached) =
                BuildAudioLengthFrames(effectiveAttached, fps);
        }

        using WorkflowBridge bridge = BridgeSync.For(g);

        JArray dynamicLengthFrames = controlNetLengthFrames ?? audioLengthFrames;
        JToken latentLength = dynamicLengthFrames is null
            ? new JValue(frames)
            : dynamicLengthFrames?.DeepClone() as JArray;

        EmptyLTXVLatentVideoNode emptyNode = latentNodeId is null
            ? bridge.AddNode(new EmptyLTXVLatentVideoNode())
            : bridge.AddNode(new EmptyLTXVLatentVideoNode(), latentNodeId);
        emptyNode.With(
            Width: width,
            Height: height,
            BatchSize: 1);
        emptyNode.Length.SetFromToken(bridge, latentLength);

        WGNodeData stageLatent = new(
            emptyNode.LATENT.ToPath(),
            g,
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat)
        {
            Width = width,
            Height = height,
            Frames = dynamicLengthFrames is null ? frames : null,
            FPS = fps,
            AttachedAudio = effectiveAttached
        };
        WGNodeData withAudio = stageLatent.EnsureHasAudioIfNeeded(genInfo.Vae, g.CurrentAudioVae);
        PatchEmptyLatentAudioAfterEnsure(stageLatent, withAudio, fps, dynamicLengthFrames);

        return withAudio;
    }

    private (JArray FramesConnection, WGNodeData EffectiveAudio) BuildAudioLengthFrames(
        WGNodeData attachedAudio,
        int fps)
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        JToken lengthFramesAudioSource = LtxAudioPathResolution.ResolveLengthToFramesAudioSource(
            bridge,
            attachedAudio.Path,
            null);

        SwarmAudioLengthToFramesNode lengthToFrames =
            bridge.AddNode(new SwarmAudioLengthToFramesNode()).With(FrameRate: fps);
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

    private WGNodeData EnsureHasAudio(
        WGNodeData latent,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageContext stageContext,
        WGNodeData sourceMedia)
    {
        int fps = LtxStageRuntimeSettings.ResolveFps(g, genInfo, sourceMedia);
        WGNodeData nativeHandoff = LtxDecodedAudioHandoff.PreferNativeLatent(g, latent);
        WGNodeData attachedAudio = nativeHandoff.AttachedAudio ?? sourceMedia?.AttachedAudio;
        WGNodeData conditionedAudio = ApplyPendingAudioConditioning(
            stageContext.ClipContext,
            attachedAudio,
            out bool applied);
        if (applied)
        {
            nativeHandoff = nativeHandoff.Duplicate();
            nativeHandoff.AttachedAudio = conditionedAudio;
        }
        WGNodeData withAudio = nativeHandoff.EnsureHasAudioIfNeeded(
            genInfo.Vae,
            g.CurrentAudioVae);
        PatchEmptyLatentAudioAfterEnsure(nativeHandoff, withAudio, fps);
        withAudio.FPS = fps;

        return withAudio;
    }

    private WGNodeData ApplyPendingAudioConditioning(
        ClipContext clipContext,
        WGNodeData attachedAudio,
        out bool applied)
    {
        LtxPendingAudioConditioningState pending = clipContext.PendingAudioConditioning;
        applied = false;
        WGNodeData pendingAudio = pending.Audio ?? attachedAudio;
        if (!pending.IsPending
            || pendingAudio.DataType == WGNodeData.DT_LATENT_AUDIO)
        {
            return attachedAudio;
        }

        WGNodeData conditioned = AudioPreserveWindowBuilder.TryBuild(
            g,
            pendingAudio,
            pending.PreserveWindows,
            stableIdSlot: clipContext.PlannedClip.ClipId + 1);
        if (conditioned is null)
        {
            return attachedAudio;
        }

        pending.Clear();
        applied = true;
        return conditioned;
    }

    private static bool ShouldMatchStageLengthToAudio(AudioPlan audio) =>
        audio.LengthOwner == AudioLengthOwner.Audio;

    // Prefer planned dimensions because root media can retain its source size.
    private (int Width, int Height) ResolveStageLatentDims(
        StageContext stageContext,
        WGNodeData sourceMedia)
    {
        ClipDimensionState dims = stageContext.ClipContext.Dimensions;
        int width = dims.Width > 0
            ? dims.Width
            : sourceMedia?.Width ?? g.UserInput.GetImageWidth();
        int height = dims.Height > 0
            ? dims.Height
            : sourceMedia?.Height ?? g.UserInput.GetImageHeight();
        return (Math.Max(width, 16), Math.Max(height, 16));
    }

    private void PatchEmptyLatentAudioAfterEnsure(
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
        if (bridge.Graph.GetNode<LTXVEmptyLatentAudioNode>($"{audioPath[0]}")
            is not LTXVEmptyLatentAudioNode emptyAudio)
        {
            return;
        }
        if (framesConnection is JArray framesArr)
        {
            emptyAudio.FramesNumber.TryConnectFromPath(bridge, framesArr);
        }
        emptyAudio.FrameRate.Set(frameRate);
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
