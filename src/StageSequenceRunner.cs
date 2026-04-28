using Newtonsoft.Json.Linq;
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
    RootVideoStageTakeover rootVideoStageTakeover,
    RootVideoStageResizer rootVideoStageResizer,
    MultiClipParallelMerger multiClipParallelMerger,
    LtxManager ltxManager)
{
    private const int IntermediateStageSaveId = 52100;

    private sealed class RunContext
    {
        public AudioStageDetector.Detection NativeAudioDetection { get; init; }
        public IReadOnlyDictionary<int, AudioStageDetector.Detection> ClipAudios { get; init; }
        public IReadOnlyDictionary<int, AudioStageDetector.Detection> UploadedAudios { get; init; }
        public bool RootStageTakeover { get; init; }
        public int? PreparedClipId { get; set; }
    }

    public void Run(
        IReadOnlyList<JsonParser.StageSpec> stages,
        AudioStageDetector.Detection detectedAudio = null,
        IReadOnlyDictionary<int, AudioStageDetector.Detection> clipAudios = null,
        IReadOnlyDictionary<int, AudioStageDetector.Detection> uploadedAudios = null,
        bool rootStageTakeover = false)
    {
        RunContext context = new()
        {
            NativeAudioDetection = detectedAudio ?? BuildCurrentMediaAudioDetection(g),
            ClipAudios = clipAudios,
            UploadedAudios = uploadedAudios,
            RootStageTakeover = rootStageTakeover
        };
        List<int> usedSectionIds = [];
        bool parallelMultiClip = StagesUseMultipleClipIds(stages);
        List<WGNodeData> clipParallelOutputs = [];
        try
        {
            if (context.RootStageTakeover)
            {
                rootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia();
            }
            CaptureReference(StageRefStore.StageKind.Generated);
            WGNodeData parallelClipSourceMedia = g.CurrentMedia?.Duplicate();
            WGNodeData parallelClipSourceVae = g.CurrentVae?.Duplicate();
            if (parallelMultiClip)
            {
                g.NodeHelpers[MultiClipParallelMerger.NodeHelperKey] = "1";
            }

            for (int i = 0; i < stages.Count; i++)
            {
                JsonParser.StageSpec stage = stages[i];
                if (parallelMultiClip
                    && i > 0
                    && stage.ClipId != stages[i - 1].ClipId)
                {
                    if (parallelClipSourceMedia is null)
                    {
                        Logs.Error(
                            "VideoStages: parallel clips require root media before the first stage. "
                            + "Stopping further stages.");
                        break;
                    }

                    g.CurrentMedia = parallelClipSourceMedia.Duplicate();
                    if (parallelClipSourceVae is not null)
                    {
                        g.CurrentVae = parallelClipSourceVae.Duplicate();
                    }
                }

                PrepareClipAudio(stage, context);
                StageRefStore.StageRef guideRef = TryResolveGuideReference(stage);
                if (guideRef is null)
                {
                    int clipId = stage.ClipId;
                    Logs.Warning(
                        $"VideoStages: Skipping all remaining stages for clip {clipId} after stage {stage.Id} "
                        + "could not resolve its image reference.");
                    while (i + 1 < stages.Count && stages[i + 1].ClipId == clipId)
                    {
                        i++;
                    }
                    continue;
                }

                int sectionId = VideoStagesExtension.SectionIdForStage(stage.Id);
                usedSectionIds.Add(sectionId);
                PrepareStageOverrides(stage, sectionId);
                singleStageRunner.RunStage(stage, sectionId, guideRef, store);
                CaptureReference(StageRefStore.StageKind.Stage, stage.Id);

                if (parallelMultiClip
                    && (i == stages.Count - 1 || stages[i + 1].ClipId != stage.ClipId))
                {
                    clipParallelOutputs.Add(g.CurrentMedia.Duplicate());
                }

                if (g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
                    && stage.Id < stages.Count - 1)
                {
                    g.CurrentMedia.SaveOutput(
                        g.CurrentVae,
                        g.CurrentAudioVae,
                        g.GetStableDynamicID(IntermediateStageSaveId, stage.Id));
                }
            }

            if (parallelMultiClip && clipParallelOutputs.Count > 1)
            {
                JArray rootVideoPath = StageRunner.CopyPath(parallelClipSourceMedia?.Path as JArray);
                multiClipParallelMerger.Apply(clipParallelOutputs, rootVideoPath);
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

    private void PrepareClipAudio(JsonParser.StageSpec stage, RunContext context)
    {
        if (context.PreparedClipId == stage.ClipId || g.CurrentMedia is null)
        {
            return;
        }

        context.PreparedClipId = stage.ClipId;
        WGNodeData currentMedia = g.CurrentMedia.Duplicate();
        bool suppressNative = context.RootStageTakeover
            && rootVideoStageTakeover.ShouldReplaceTextToVideoRootStage(stage);
        AudioStageDetector.Detection clipAudio = ClipAudioWorkflowHelper.ResolveClipAudioDetection(
            stage.ClipId,
            stage.ClipAudioSource,
            context.NativeAudioDetection,
            context.ClipAudios,
            context.UploadedAudios,
            suppressNative,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.StageSpec);
        currentMedia.AttachedAudio = clipAudio?.Audio;
        g.CurrentMedia = currentMedia;
        if (context.RootStageTakeover
            && ClipAudioWorkflowHelper.ShouldMatchVideoLengthForTryInjectAudio(
                stage.ClipAudioSource,
                stage.ClipLengthFromAudio,
                restrictLengthMatchToUploadOrAce: true))
        {
            _ = ltxManager.TryInjectAudio(clipAudio);
        }
    }

    private void CaptureReference(StageRefStore.StageKind kind, int? index = null)
    {
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        ltxManager.ApplyPostVideoChainCaptureIfPresent(ref referenceMedia, ref referenceVae);
        store.Capture(kind, index, referenceMedia, referenceVae);
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
        if (stage.ClipFPS.HasValue && stage.ClipFPS.Value > 0)
        {
            g.UserInput.Set(T2IParamTypes.VideoFPS, stage.ClipFPS.Value, sectionId);
        }
        if (stage.ClipWidth.HasValue && stage.ClipWidth.Value > 0)
        {
            g.UserInput.Set(T2IParamTypes.Width, stage.ClipWidth.Value, sectionId);
        }
        if (stage.ClipHeight.HasValue && stage.ClipHeight.Value > 0)
        {
            g.UserInput.Set(T2IParamTypes.Height, stage.ClipHeight.Value, sectionId);
        }
    }

    private StageRefStore.StageRef TryResolveGuideReference(JsonParser.StageSpec stage)
    {
        if (stage.ImageReference.Equals("Base", StringComparison.Ordinal))
        {
            return WarnIfMissing(
                store.Base,
                "VideoStages: ImageReference 'Base' requested, but no base reference exists.");
        }
        if (stage.ImageReference.Equals("Refiner", StringComparison.Ordinal))
        {
            return WarnIfMissing(
                store.Refiner,
                "VideoStages: ImageReference 'Refiner' requested, but no refiner reference exists.");
        }
        if (stage.ImageReference.Equals("Generated", StringComparison.Ordinal))
        {
            if (stage.Id > 0 && store.TryGetStageRef(stage.Id - 1, out StageRefStore.StageRef previousGenerated))
            {
                return previousGenerated;
            }
            return WarnIfMissing(
                store.Generated,
                "VideoStages: ImageReference 'Generated' requested, but no generated reference exists.");
        }
        if (stage.ImageReference.Equals("PreviousStage", StringComparison.Ordinal))
        {
            if (stage.Id <= 0)
            {
                Logs.Warning(
                    "VideoStages: ImageReference 'PreviousStage' cannot be used for the first stage.");
                return null;
            }
            if (!store.TryGetStageRef(stage.Id - 1, out StageRefStore.StageRef previousStage))
            {
                Logs.Warning(
                    $"VideoStages: ImageReference 'PreviousStage' requested, but stage {stage.Id - 1} does not exist.");
                return null;
            }
            return previousStage;
        }
        if (ImageReferenceSyntax.TryParseExplicitStageIndex(stage.ImageReference, out int explicitStage))
        {
            if (!store.TryGetStageRef(explicitStage, out StageRefStore.StageRef explicitRef))
            {
                Logs.Warning(
                    $"VideoStages: ImageReference '{stage.ImageReference}' requested, but stage {explicitStage} "
                    + "does not exist.");
                return null;
            }
            return explicitRef;
        }
        if (ImageReferenceSyntax.TryParseBase2EditStageIndex(stage.ImageReference, out int editStage))
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

    private static bool StagesUseMultipleClipIds(IReadOnlyList<JsonParser.StageSpec> stages)
    {
        if (stages.Count == 0)
        {
            return false;
        }
        int firstClipId = stages[0].ClipId;
        for (int i = 1; i < stages.Count; i++)
        {
            if (stages[i].ClipId != firstClipId)
            {
                return true;
            }
        }
        return false;
    }

    private static StageRefStore.StageRef WarnIfMissing(StageRefStore.StageRef r, string message)
    {
        if (r is null)
        {
            Logs.Warning(message);
        }
        return r;
    }

    private static AudioStageDetector.Detection BuildCurrentMediaAudioDetection(WorkflowGenerator g)
    {
        if (g.CurrentMedia?.AttachedAudio is null)
        {
            return null;
        }
        return new AudioStageDetector.Detection(
            g.CurrentMedia.AttachedAudio,
            "videostages.current-media-audio",
            "CurrentMediaAttachedAudio",
            "videostages.current-media-audio",
            0);
    }
}
