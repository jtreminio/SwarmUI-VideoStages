using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.LTX2;

namespace VideoStages;

internal sealed class StageSequenceRunner(
    WorkflowGenerator g,
    StageRefStore store,
    StageRunner singleStageRunner,
    Base2EditPublishedStageRefs base2EditPublishedStageRefs,
    RootVideoStageHandoff rootVideoStageHandoff,
    RootVideoStageResizer rootVideoStageResizer,
    MultiClipParallelMerger multiClipParallelMerger,
    LtxManager ltxManager)
{
    private const int IntermediateStageSaveId = 52100;

    private readonly Dictionary<int, StageRefStore.StageRef> _stageOutputs = [];
    private StageRefStore.StageRef _previousStageRef;

    private sealed class RunContext
    {
        public WGNodeData NativeAudio { get; init; }
        public IReadOnlyDictionary<int, WGNodeData> ClipAudios { get; init; }
        public IReadOnlyDictionary<int, WGNodeData> UploadedAudios { get; init; }
        public bool RootStageHandoff { get; init; }
    }

    public void Run(
        IReadOnlyList<JsonParser.ClipWithStages> clips,
        WGNodeData nativeAudio = null,
        IReadOnlyDictionary<int, WGNodeData> clipAudios = null,
        IReadOnlyDictionary<int, WGNodeData> uploadedAudios = null,
        bool rootStageHandoff = false)
    {
        RunContext context = new()
        {
            NativeAudio = nativeAudio ?? g.CurrentMedia?.AttachedAudio,
            ClipAudios = clipAudios,
            UploadedAudios = uploadedAudios,
            RootStageHandoff = rootStageHandoff
        };
        _stageOutputs.Clear();
        _previousStageRef = null;
        List<int> usedSectionIds = [];
        bool parallelMultiClip = clips.Count > 1;
        List<WGNodeData> clipParallelOutputs = [];
        try
        {
            if (context.RootStageHandoff)
            {
                rootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia();
            }
            CaptureGeneratedReference();
            WGNodeData rootSourceMedia = g.CurrentMedia?.Duplicate();
            WGNodeData rootSourceVae = g.CurrentVae?.Duplicate();
            if (parallelMultiClip)
            {
                g.NodeHelpers[MultiClipParallelMerger.NodeHelperKey] = "1";
            }

            int totalStageCount = TotalStageCount(clips);
            bool isFirstClip = true;
            foreach (JsonParser.ClipWithStages clip in clips)
            {
                ClipContext clipContext = new(clip, rootSourceMedia, rootSourceVae);
                if (parallelMultiClip && !isFirstClip)
                {
                    if (clipContext.SourceMedia is null)
                    {
                        Logs.Error(
                            "VideoStages: parallel clips require root media before the first stage. "
                            + "Stopping further stages.");
                        break;
                    }

                    g.CurrentMedia = clipContext.SourceMedia.Duplicate();
                    if (clipContext.SourceVae is not null)
                    {
                        g.CurrentVae = clipContext.SourceVae.Duplicate();
                    }
                }
                isFirstClip = false;

                JsonParser.StageSpec firstStage = clip.Stages[0];
                ApplyControlNetClipLengthIfApplicable(firstStage);
                PrepareClipAudio(firstStage, context);

                bool clipCompleted = true;
                foreach (JsonParser.StageSpec stage in clip.Stages)
                {
                    StageRefStore.StageRef guideRef = TryResolveGuideReference(stage);
                    if (guideRef is null)
                    {
                        Logs.Warning(
                            $"VideoStages: Skipping all remaining stages for clip {stage.ClipId} after stage {stage.Id} "
                            + "could not resolve its image reference.");
                        clipCompleted = false;
                        _previousStageRef = null;
                        break;
                    }

                    int sectionId = VideoStagesExtension.SectionIdForStage(stage.Id);
                    usedSectionIds.Add(sectionId);
                    PrepareStageOverrides(stage, sectionId);
                    singleStageRunner.RunStage(stage, sectionId, guideRef, store, clipContext);
                    CaptureStageOutput(stage.Id);

                    if (stage.Id < totalStageCount - 1
                        && g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false))
                    {
                        g.CurrentMedia.SaveOutput(
                            g.CurrentVae,
                            g.CurrentAudioVae,
                            g.GetStableDynamicID(IntermediateStageSaveId, stage.Id));
                    }
                }

                if (parallelMultiClip && clipCompleted)
                {
                    clipParallelOutputs.Add(g.CurrentMedia.Duplicate());
                }
            }

            if (parallelMultiClip && clipParallelOutputs.Count > 1)
            {
                multiClipParallelMerger.Apply(clipParallelOutputs, rootSourceMedia);
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"VideoStages: Stage sequence aborted due to an unexpected error: {ex}");
        }
        finally
        {
            if (parallelMultiClip)
            {
                _ = g.NodeHelpers.Remove(MultiClipParallelMerger.NodeHelperKey);
            }

            foreach (int sectionId in usedSectionIds)
            {
                g.UserInput.SectionParamOverrides.Remove(sectionId);
            }
        }
    }

    private static int TotalStageCount(IReadOnlyList<JsonParser.ClipWithStages> clips)
    {
        int total = 0;
        foreach (JsonParser.ClipWithStages clip in clips)
        {
            total += clip.Stages.Count;
        }
        return total;
    }

    private void PrepareClipAudio(JsonParser.StageSpec stage, RunContext context)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }

        WGNodeData currentMedia = g.CurrentMedia.Duplicate();
        bool suppressNative = context.RootStageHandoff
            && rootVideoStageHandoff.ShouldReplaceTextToVideoRootStage(stage);
        WGNodeData clipAudio = ClipAudioWorkflowHelper.ResolveClipAudio(
            stage.ClipId,
            stage.ClipAudioSource,
            context.NativeAudio,
            context.ClipAudios,
            context.UploadedAudios,
            suppressNative,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.StageSpec);
        currentMedia.AttachedAudio = clipAudio;
        g.CurrentMedia = currentMedia;
        if (context.RootStageHandoff
            && ClipAudioWorkflowHelper.ShouldMatchVideoLengthForTryInjectAudio(
                stage.ClipAudioSource,
                stage.ClipLengthFromAudio && !stage.ClipLengthFromControlNet,
                restrictLengthMatchToUploadOrAce: true))
        {
            _ = ltxManager.TryInjectAudio(clipAudio);
        }
    }

    private void ApplyControlNetClipLengthIfApplicable(JsonParser.StageSpec stage)
    {
        if (stage.ClipLengthFromControlNet && VideoStageModelCompat.IsLtxV2VideoModel(stage.Model))
        {
            _ = ltxManager.TryApplyControlNetFrameCount(stage.ClipControlNetSource);
        }
    }

    private void CaptureGeneratedReference()
    {
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        ltxManager.ApplyPostVideoChainCaptureIfPresent(ref referenceMedia, ref referenceVae);
        store.Capture(StageRefStore.StageKind.Generated, referenceMedia, referenceVae);
    }

    private void CaptureStageOutput(int index)
    {
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        ltxManager.ApplyPostVideoChainCaptureIfPresent(ref referenceMedia, ref referenceVae);
        StageRefStore.StageRef captured = new(referenceMedia, referenceVae);
        _stageOutputs[index] = captured;
        _previousStageRef = captured;
    }

    private void PrepareStageOverrides(JsonParser.StageSpec stage, int sectionId)
    {
        g.UserInput.SectionParamOverrides.Remove(sectionId);
        g.UserInput.Set(T2IParamTypes.VideoModel.Type, stage.Model, sectionId);
        g.UserInput.Set(T2IParamTypes.VideoSteps, stage.Steps, sectionId);
        g.UserInput.Set(T2IParamTypes.Steps, stage.Steps, sectionId);
        g.UserInput.Set(T2IParamTypes.VideoCFG, stage.CfgScale, sectionId);
        g.UserInput.Set(T2IParamTypes.CFGScale, stage.CfgScale, sectionId);
        g.UserInput.Set(ComfyUIBackendExtension.SamplerParam.Type, stage.Sampler, sectionId);
        g.UserInput.Set(ComfyUIBackendExtension.SchedulerParam.Type, stage.Scheduler, sectionId);
        if (JsonParser.IsUsableVaeValue(stage.Vae))
        {
            g.UserInput.Set(T2IParamTypes.VAE.Type, stage.Vae, sectionId);
        }
        if (stage.ClipFrames.HasValue && stage.ClipFrames.Value > 0)
        {
            g.UserInput.Set(T2IParamTypes.VideoFrames, stage.ClipFrames.Value, sectionId);
        }
        if (stage.ClipFPS > 0)
        {
            g.UserInput.Set(T2IParamTypes.VideoFPS, stage.ClipFPS, sectionId);
        }
        if (stage.ClipWidth > 0)
        {
            g.UserInput.Set(T2IParamTypes.Width, stage.ClipWidth, sectionId);
        }
        if (stage.ClipHeight > 0)
        {
            g.UserInput.Set(T2IParamTypes.Height, stage.ClipHeight, sectionId);
        }
    }

    private StageRefStore.StageRef TryResolveGuideReference(JsonParser.StageSpec stage)
    {
        if (StringUtils.Equals(stage.ImageReference, "Base"))
        {
            return WarnIfMissing(
                store.Base,
                "VideoStages: ImageReference 'Base' requested, but no base reference exists.");
        }
        if (StringUtils.Equals(stage.ImageReference, "Refiner"))
        {
            return WarnIfMissing(
                store.Refiner,
                "VideoStages: ImageReference 'Refiner' requested, but no refiner reference exists.");
        }
        if (StringUtils.Equals(stage.ImageReference, "Generated"))
        {
            if (_previousStageRef is not null)
            {
                return _previousStageRef;
            }
            return WarnIfMissing(
                store.Generated,
                "VideoStages: ImageReference 'Generated' requested, but no generated reference exists.");
        }
        if (StringUtils.Equals(stage.ImageReference, "PreviousStage"))
        {
            if (_previousStageRef is null)
            {
                Logs.Warning(
                    "VideoStages: ImageReference 'PreviousStage' cannot be used for the first stage.");
                return null;
            }
            return _previousStageRef;
        }
        if (ImageReference.TryParseExplicitStageIndex(stage.ImageReference, out int explicitStage))
        {
            if (!_stageOutputs.TryGetValue(explicitStage, out StageRefStore.StageRef explicitRef))
            {
                Logs.Warning(
                    $"VideoStages: ImageReference '{stage.ImageReference}' requested, but stage {explicitStage} "
                    + "does not exist.");
                return null;
            }
            return explicitRef;
        }
        if (ImageReference.TryParseBase2EditStageIndex(stage.ImageReference, out int editStage))
        {
            if (!base2EditPublishedStageRefs.TryGetStageRef(editStage, out StageRefStore.StageRef publishedEditRef))
            {
                Logs.Warning(
                    $"VideoStages: ImageReference '{stage.ImageReference}' requested, but Base2Edit stage "
                    + $"{editStage} does not exist.");
                return null;
            }
            return publishedEditRef;
        }
        Logs.Warning($"VideoStages: Unknown ImageReference value '{stage.ImageReference}'.");
        return null;
    }

    private static StageRefStore.StageRef WarnIfMissing(StageRefStore.StageRef r, string message)
    {
        if (r is null)
        {
            Logs.Warning(message);
        }
        return r;
    }

}
