using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;

using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>Prepares the source graph and generation frame consumed by the LTX stage executor.</summary>
internal sealed class StageFramePreparer(
    WorkflowGenerator g,
    StageUpscaleGraphBuilder upscaleGraphBuilder,
    PlannedStagePromptResolver promptResolver)
{
    public StageFrame Prepare(
        StagePlan stage,
        int sectionId,
        ClipContext clipContext,
        bool requiresDedicatedOutput,
        RootExecutionPolicy rootPolicy)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(clipContext);
        ArgumentNullException.ThrowIfNull(rootPolicy);

        WGNodeData currentMedia = g.CurrentMedia
            ?? throw VideoStagesInvariant.Failure(
                $"VideoStages: stage {stage.StageId} has no input media.");
        JArray priorOutputPath = CopyPath(currentMedia.Path);
        LtxAudioReuseState.PrepareReusableAudio(g, clipContext, stage);
        bool replacesTextToVideoRoot = rootPolicy.ReplacesTextToVideoRootStage(
            stage,
            clipContext.PlannedClip);
        LtxPostVideoChainCapture postVideoChain = replacesTextToVideoRoot
            ? null
            : LtxPostVideoChainCapture.TryCapture(g, clipContext, stage);
        WGNodeData sourceMedia = replacesTextToVideoRoot
            ? CloneMedia(g.CurrentMedia)
            : upscaleGraphBuilder.Apply(clipContext, stage, sectionId, postVideoChain);
        if (sourceMedia is null)
        {
            throw VideoStagesInvariant.Failure(
                $"VideoStages: stage {stage.StageId} could not resolve its source media.");
        }

        WorkflowGenerator.ImageToVideoGenInfo genInfo = BuildGenInfo(
            clipContext,
            stage,
            sectionId,
            sourceMedia,
            replacesTextToVideoRoot);
        return new StageFrame(
            stage,
            clipContext,
            priorOutputPath,
            replacesTextToVideoRoot,
            postVideoChain,
            sourceMedia,
            genInfo,
            requiresDedicatedOutput);
    }

    private WorkflowGenerator.ImageToVideoGenInfo BuildGenInfo(
        ClipContext clipContext,
        StagePlan stage,
        int sectionId,
        WGNodeData sourceMedia,
        bool replacesTextToVideoRoot)
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
            throw VideoStagesInvariant.Failure(
                $"VideoStages: stage {stage.StageId} could not resolve LTX video model "
                + $"'{stage.ResolvedModel.ModelName}'.");
        }
        // The host loader key does not include the stage section or its scoped LoRA state, so LTX
        // must rebuild even when shared graph cleanup has already removed every stale tuple.
        VideoGraphHelpers.RemoveCached(
            g,
            $"modelloader_{videoModel.Name}_image2video");

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
                replacesTextToVideoRoot),
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
        bool replacesTextToVideoRoot)
    {
        if (clipContext.IncomingContinueHandleFrames > 0)
        {
            return clipContext.GenerationFrames;
        }
        if (!replacesTextToVideoRoot && sourceMedia.Frames.HasValue)
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
