using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.LTX2;

namespace VideoStages;

public class StageSequenceRunner(
    WorkflowGenerator g,
    StageRefStore store,
    IReadOnlyList<JsonParser.StageSpec> stages,
    AudioStageDetector.Detection detectedAudio = null,
    IReadOnlyDictionary<int, AudioStageDetector.Detection> uploadedAudios = null,
    bool rootStageTakeover = false)
{
    private const int IntermediateStageSaveId = 52100;

    private readonly StageRunner _singleStageRunner = new(g);
    private readonly AudioStageDetector.Detection _nativeAudioDetection =
        detectedAudio ?? BuildCurrentMediaAudioDetection(g);
    private int? _preparedClipId = null;
    private readonly bool _rootStageTakeover = rootStageTakeover;

    public void Run()
    {
        List<int> usedSectionIds = [];
        bool parallelMultiClip = stages.Count > 0 && stages.Select(s => s.ClipId).Distinct().Count() > 1;
        List<WGNodeData> clipParallelOutputs = [];
        try
        {
            if (_rootStageTakeover)
            {
                RootVideoStageResizer.ApplyConfiguredRootStageResolutionToCurrentMedia(g);
            }
            CaptureReference(StageRefStore.StageKind.Generated);
            WGNodeData parallelClipSourceMedia = g.CurrentMedia?.Duplicate();
            WGNodeData parallelClipSourceVae = g.CurrentVae?.Duplicate();
            if (parallelMultiClip)
            {
                g.NodeHelpers[MultiClipParallelWorkflowFlags.NodeHelperKey] = "1";
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
                        throw new InvalidOperationException("VideoStages: parallel clips require root media before the first stage.");
                    }

                    g.CurrentMedia = parallelClipSourceMedia.Duplicate();
                    if (parallelClipSourceVae is not null)
                    {
                        g.CurrentVae = parallelClipSourceVae.Duplicate();
                    }
                }

                PrepareClipAudio(stage);
                StageRefStore.StageRef guideRef = ResolveGuideReference(stage);

                int sectionId = VideoStagesExtension.SectionIdForStage(stage.Id);
                usedSectionIds.Add(sectionId);
                PrepareStageOverrides(stage, sectionId);
                _singleStageRunner.RunStage(stage, sectionId, guideRef, store);
                CaptureReference(StageRefStore.StageKind.Stage, stage.Id);

                if (parallelMultiClip
                    && (i == stages.Count - 1 || stages[i + 1].ClipId != stage.ClipId))
                {
                    clipParallelOutputs.Add(g.CurrentMedia.Duplicate());
                }

                if (g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false) && stage.Id < stages.Count - 1)
                {
                    g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, g.GetStableDynamicID(IntermediateStageSaveId, stage.Id));
                }
            }

            if (parallelMultiClip && clipParallelOutputs.Count > 1)
            {
                JArray rootVideoPath = parallelClipSourceMedia?.Path is JArray rp && rp.Count == 2
                    ? new JArray(rp[0], rp[1])
                    : null;
                MultiClipParallelMerger.Apply(g, clipParallelOutputs, rootVideoPath);
            }
        }
        finally
        {
            if (parallelMultiClip)
            {
                _ = g.NodeHelpers.Remove(MultiClipParallelWorkflowFlags.NodeHelperKey);
            }

            foreach (int sectionId in usedSectionIds)
            {
                g.UserInput.SectionParamOverrides.Remove(sectionId);
            }
        }
    }

    private void PrepareClipAudio(JsonParser.StageSpec stage)
    {
        if (_preparedClipId == stage.ClipId || g.CurrentMedia is null)
        {
            return;
        }

        _preparedClipId = stage.ClipId;
        WGNodeData currentMedia = g.CurrentMedia.Duplicate();
        currentMedia.AttachedAudio = ResolveClipAudio(stage)?.Audio;
        g.CurrentMedia = currentMedia;
    }

    private AudioStageDetector.Detection ResolveClipAudio(JsonParser.StageSpec stage)
    {
        string source = $"{stage.ClipAudioSource ?? VideoStagesExtension.AudioSourceNative}".Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }
        if (string.Equals(source, VideoStagesExtension.AudioSourceUpload, StringComparison.OrdinalIgnoreCase))
        {
            if (uploadedAudios is null)
            {
                return null;
            }
            return uploadedAudios.TryGetValue(stage.ClipId, out AudioStageDetector.Detection detection) ? detection : null;
        }
        return _nativeAudioDetection;
    }

    private void CaptureReference(StageRefStore.StageKind kind, int? index = null)
    {
        WGNodeData referenceMedia = g.CurrentMedia;
        WGNodeData referenceVae = g.CurrentVae;
        PostVideoChain postVideoChain = PostVideoChain.TryCapture(g);
        if (postVideoChain is not null)
        {
            referenceMedia = postVideoChain.CreateStageInput();
            referenceVae = postVideoChain.CreateStageInputVae();
        }
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
        if (stage.ClipWidth.HasValue && stage.ClipWidth.Value > 0)
        {
            g.UserInput.Set(T2IParamTypes.Width, stage.ClipWidth.Value, sectionId);
        }
        if (stage.ClipHeight.HasValue && stage.ClipHeight.Value > 0)
        {
            g.UserInput.Set(T2IParamTypes.Height, stage.ClipHeight.Value, sectionId);
        }
    }

    private StageRefStore.StageRef ResolveGuideReference(JsonParser.StageSpec stage)
    {
        if (stage.ImageReference.Equals("Base", StringComparison.Ordinal))
        {
            return store.Base ?? throw new InvalidOperationException("ImageReference 'Base' requested, but no base reference exists.");
        }
        if (stage.ImageReference.Equals("Refiner", StringComparison.Ordinal))
        {
            return store.Refiner ?? throw new InvalidOperationException("ImageReference 'Refiner' requested, but no refiner reference exists.");
        }
        if (stage.ImageReference.Equals("Generated", StringComparison.Ordinal))
        {
            // "Generated" follows the latest generated output entering this stage.
            if (stage.Id > 0 && store.TryGetStageRef(stage.Id - 1, out StageRefStore.StageRef previousGenerated))
            {
                return previousGenerated;
            }
            return store.Generated ?? throw new InvalidOperationException("ImageReference 'Generated' requested, but no generated reference exists.");
        }
        if (stage.ImageReference.Equals("PreviousStage", StringComparison.Ordinal))
        {
            if (stage.Id <= 0)
            {
                throw new InvalidOperationException("ImageReference 'PreviousStage' cannot be used for the first stage.");
            }
            if (!store.TryGetStageRef(stage.Id - 1, out StageRefStore.StageRef previousStage))
            {
                throw new InvalidOperationException($"ImageReference 'PreviousStage' requested, but stage {stage.Id - 1} does not exist.");
            }
            return previousStage;
        }
        if (ImageReferenceSyntax.TryParseExplicitStageIndex(stage.ImageReference, out int explicitStage))
        {
            if (!store.TryGetStageRef(explicitStage, out StageRefStore.StageRef explicitRef))
            {
                throw new InvalidOperationException($"ImageReference '{stage.ImageReference}' requested, but stage {explicitStage} does not exist.");
            }
            return explicitRef;
        }
        if (ImageReferenceSyntax.TryParseBase2EditStageIndex(stage.ImageReference, out int editStage))
        {
            if (!Base2EditPublishedStageRefs.TryGetStageRef(g, editStage, out StageRefStore.StageRef publishedEditRef))
            {
                throw new InvalidOperationException($"ImageReference '{stage.ImageReference}' requested, but Base2Edit stage {editStage} does not exist.");
            }
            return publishedEditRef;
        }
        throw new InvalidOperationException($"Unknown ImageReference value '{stage.ImageReference}'.");
    }

    private static AudioStageDetector.Detection BuildCurrentMediaAudioDetection(WorkflowGenerator g)
    {
        if (g?.CurrentMedia?.AttachedAudio is null)
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
