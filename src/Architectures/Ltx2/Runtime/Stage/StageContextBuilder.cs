using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Architectures.Ltx2.Runtime.Chain;
using VideoStages.Architectures.Ltx2.Runtime.Audio;

namespace VideoStages.Architectures.Ltx2.Runtime.Stage;

/// <summary>Builds the source graph and per-stage context the LTX stage executor consumes.</summary>
internal sealed class StageContextBuilder(
    WorkflowGenerator g,
    StageSourceMediaResolver sourceMediaResolver,
    PlannedStagePromptResolver promptResolver)
{
    public StageContext Build(
        StagePlan stage,
        int sectionId,
        ClipContext clipContext,
        bool requiresDedicatedOutput)
    {
        JArray priorOutputPath = CopyPath(g.CurrentMedia.Path);
        bool claimsTextToVideoRoot = clipContext.Plan.Root.StageClaimsTextToVideoRoot(
            stage,
            clipContext.PlannedClip);
        (LtxPostVideoChain postVideoChain, WGNodeData sourceMedia) = ResolveStageSource(
            stage,
            sectionId,
            clipContext,
            claimsTextToVideoRoot);
        if (sourceMedia is null)
        {
            throw Invariant.Failure(
                $"stage {stage.StageId} could not resolve its source media.");
        }

        WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(
            clipContext,
            stage,
            sectionId,
            sourceMedia,
            claimsTextToVideoRoot);
        return new StageContext(
            stage,
            clipContext,
            priorOutputPath,
            claimsTextToVideoRoot,
            postVideoChain,
            sourceMedia,
            genInfo,
            requiresDedicatedOutput);
    }

    public (LtxPostVideoChain Chain, WGNodeData SourceMedia) ResolveStageSource(
        StagePlan stage,
        int sectionId,
        ClipContext clipContext,
        bool claimsTextToVideoRoot)
    {
        LtxAudioReuseState.PrepareReusableAudio(g, clipContext, stage);
        LtxPostVideoChain postVideoChain = claimsTextToVideoRoot
            ? null
            : LtxPostVideoChain.TryCapture(g, clipContext, stage);
        WGNodeData sourceMedia = claimsTextToVideoRoot
            ? CloneMedia(g.CurrentMedia)
            : sourceMediaResolver.Resolve(clipContext, stage, sectionId, postVideoChain);
        return (postVideoChain, sourceMedia);
    }

    private WorkflowGenerator.ImageToVideoGenInfo BuildGenInfo(
        ClipContext clipContext,
        StagePlan stage,
        int sectionId,
        WGNodeData sourceMedia,
        bool claimsTextToVideoRoot)
    {
        ClipPlan clip = clipContext.PlannedClip;
        Ltx2StagePayload payload = stage.RequireLtx2Payload();
        ClipDimensionState dimensions = clipContext.Dimensions;
        T2IModel videoModel = g.UserInput.Get(
            T2IParamTypes.VideoModel,
            null,
            sectionId: sectionId);
        if (videoModel is null)
        {
            throw Invariant.Failure(
                $"stage {stage.StageId} could not resolve LTX video model "
                + $"'{stage.ResolvedModel.ModelName}'.");
        }
        bool sourceIsVideo = sourceMedia.DataType == WGNodeData.DT_VIDEO;
        (int batchIndex, int batchLen) = sourceIsVideo ? (0, 1) : (-1, -1);
        (string positivePrompt, string negativePrompt) = promptResolver.Resolve(clip, stage);
        (int stageWidth, int stageHeight) = StageDimensionRules.SnapForIcLora(
            stage,
            sourceMedia.Width ?? dimensions.Width,
            sourceMedia.Height ?? dimensions.Height);

        return new WorkflowGenerator.ImageToVideoGenInfo
        {
            Generator = g,
            VideoModel = videoModel,
            VideoSwapModel = null,
            VideoSwapPercent = 0.5,
            Frames = ResolveFrames(
                clipContext,
                sourceMedia,
                sectionId,
                claimsTextToVideoRoot),
            VideoCFG = payload.Core.CfgScale,
            VideoFPS = clipContext.Plan.FramesPerSecond,
            Width = stageWidth,
            Height = stageHeight,
            Prompt = positivePrompt,
            NegativePrompt = negativePrompt,
            Steps = payload.Core.Steps,
            Seed = g.UserInput.Get(T2IParamTypes.Seed) + 42 + stage.StageId,
            BatchIndex = batchIndex,
            BatchLen = batchLen,
            ContextID = sectionId,
            VideoEndFrame = g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
        };
    }

    private int? ResolveFrames(
        ClipContext clipContext,
        WGNodeData sourceMedia,
        int sectionId,
        bool claimsTextToVideoRoot)
    {
        if (clipContext.IncomingContinueHandleFrames > 0)
        {
            return clipContext.GenerationFrames;
        }
        if (!claimsTextToVideoRoot && sourceMedia.Frames.HasValue)
        {
            return sourceMedia.Frames;
        }
        if (g.UserInput.TryGet(
            T2IParamTypes.VideoFrames,
            out int explicitFrames,
            sectionId: sectionId))
        {
            return explicitFrames;
        }
        if (g.UserInput.TryGet(
            T2IParamTypes.Text2VideoFrames,
            out int textToVideoFrames,
            sectionId: sectionId))
        {
            return textToVideoFrames;
        }
        return sourceMedia.Frames;
    }

    internal static JArray CopyPath(JArray path)
    {
        return path is { Count: 2 }
            ? new JArray(path[0], path[1])
            : null;
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
}
