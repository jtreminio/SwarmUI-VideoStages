using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;
using VideoStages.Generated;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxStageLatentAudioFactory(WorkflowGenerator g)
{
    internal WGNodeData CreateEmpty(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia,
        JArray controlNetLengthFrames = null)
    {
        (int width, int height) = ResolveStageLatentDims(stageFrame, sourceMedia);
        int frames = genInfo.Frames
            ?? sourceMedia?.Frames
            ?? LtxStageRuntimeSettings.DefaultFrameCount;
        WGNodeData attachedAudio = ApplyPendingAudioConditioning(
            stageFrame.ClipContext,
            sourceMedia?.AttachedAudio,
            out _);
        return CreateEmpty(
            stageFrame.ClipContext.PlannedClip?.Audio
                ?? throw VideoStagesInvariant.Failure(
                    "LTX stage execution requires the compiled clip plan."),
            genInfo,
            sourceMedia,
            width,
            height,
            frames,
            attachedAudio,
            controlNetLengthFrames);
    }

    internal WGNodeData CreateEmpty(
        AudioPlan audio,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia,
        int width,
        int height,
        int frames,
        WGNodeData attachedAudio,
        JArray controlNetLengthFrames = null)
    {
        int fps = LtxStageRuntimeSettings.ResolveFps(g, genInfo, sourceMedia);
        JArray audioLengthFrames = null;
        WGNodeData effectiveAttached = attachedAudio;

        if (controlNetLengthFrames is null
            && ShouldMatchStageLengthToAudio(audio)
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

        EmptyLTXVLatentVideoNode emptyNode = bridge.AddNode(new EmptyLTXVLatentVideoNode());
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

    internal (JArray FramesConnection, WGNodeData EffectiveAudio) BuildAudioLengthFrames(
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

    internal WGNodeData EnsureHasAudio(
        WGNodeData latent,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia)
    {
        int fps = LtxStageRuntimeSettings.ResolveFps(g, genInfo, sourceMedia);
        WGNodeData nativeHandoff = LtxDecodedAudioHandoff.PreferNativeLatent(g, latent);
        WGNodeData attachedAudio = nativeHandoff.AttachedAudio ?? sourceMedia?.AttachedAudio;
        WGNodeData conditionedAudio = ApplyPendingAudioConditioning(
            stageFrame.ClipContext,
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

        WGNodeData conditioned = LtxAudioPreserveWindowBuilder.TryBuild(
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

    internal JArray TryResolveControlNetLengthFrames(ClipPlan clip)
    {
        if (clip.Audio.Length.Owner != AudioLengthOwner.ControlNet)
        {
            return null;
        }
        if (clip.ArchitecturePayload is not IArchitectureControlNetSourcePlan
            { ControlNetSourceIndex: int sourceIndex })
        {
            // Without a source, the authored clip length remains in effect.
            return null;
        }
        return new LtxControlNetMediaNormalizer(g).TryCreateFrameCount(
            sourceIndex,
            out JArray framesConnection)
            ? framesConnection
            : null;
    }

    internal static bool ShouldMatchStageLengthToAudio(AudioPlan audio) =>
        audio.Length.Owner == AudioLengthOwner.Audio
        && audio.Base.Kind is AudioSourceKind.Upload
            or AudioSourceKind.AceStepFun
            or AudioSourceKind.ControlNet;

    // Prefer planned dimensions because root media can retain its source size.
    private (int Width, int Height) ResolveStageLatentDims(
        StageFrame stageFrame,
        WGNodeData sourceMedia)
    {
        ClipDimensionState dims = stageFrame.ClipContext.Dimensions;
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
}
