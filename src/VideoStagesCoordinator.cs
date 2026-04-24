using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;

namespace VideoStages;

public class VideoStagesCoordinator(WorkflowGenerator g)
{
    private const int FinalStageSaveId = 52200;

    public void CaptureBase()
    {
        if (!ShouldCaptureRootReferences())
        {
            return;
        }

        new StageRefStore(g).Capture(StageRefStore.StageKind.Base);
    }

    public void CaptureRefiner()
    {
        if (!ShouldCaptureRootReferences())
        {
            return;
        }

        new StageRefStore(g).Capture(StageRefStore.StageKind.Refiner);
    }

    public void RunConfiguredStages()
    {
        if (!HasRootVideoModel())
        {
            RootVideoStageTakeover.CleanupSynthesizedRootVideoStageModel(g);
            return;
        }

        try
        {
            JsonParser parser = new(g);
            List<JsonParser.ClipSpec> clips = parser.ParseClips();
            List<JsonParser.StageSpec> stages = parser.ParseStages();
            bool rootStageTakeover = RootVideoStageTakeover.ShouldTakeOverRootStage(g);
            if (stages.Count == 0)
            {
                TryInjectConfiguredAudio(parser, clips);
                return;
            }

            AudioStageDetector audioDetector = new(g);
            AudioStageDetector.Detection detectedAudio = audioDetector.Detect();
            IReadOnlyDictionary<int, AudioStageDetector.Detection> clipAudios = BuildPerClipAudioDetections(audioDetector, clips);
            IReadOnlyDictionary<int, AudioStageDetector.Detection> uploadedAudios = BuildPerClipUploadDetections(parser, clips);
            AceStepFunAudioSavePruner.Apply(g, clips);
            if (!rootStageTakeover)
            {
                AudioStageDetector.Detection firstClipAudio = ResolveClipAudioSource(stages[0].ClipId, stages[0].ClipAudioSource, detectedAudio, clipAudios, uploadedAudios);
                _ = new LTX2.LtxAudioInjector(g).TryInject(firstClipAudio);
            }

            new StageSequenceRunner(g, new StageRefStore(g), stages, detectedAudio, clipAudios, uploadedAudios, rootStageTakeover).Run();
            EnsureFinalStageOutputSaved();
        }
        finally
        {
            RootVideoStageTakeover.CleanupSynthesizedRootVideoStageModel(g);
        }
    }

    private void TryInjectConfiguredAudio(JsonParser parser, List<JsonParser.ClipSpec> clips)
    {
        AudioStageDetector audioDetector = new(g);
        AudioStageDetector.Detection detectedAudio = audioDetector.Detect();
        LTX2.LtxAudioInjector ltxAudioInjector = new(g);
        if (clips.Count == 0)
        {
            ltxAudioInjector.TryInject(detectedAudio);
            return;
        }

        IReadOnlyDictionary<int, AudioStageDetector.Detection> clipAudios = BuildPerClipAudioDetections(audioDetector, clips);
        IReadOnlyDictionary<int, AudioStageDetector.Detection> uploadedAudios = BuildPerClipUploadDetections(parser, clips);
        AceStepFunAudioSavePruner.Apply(g, clips);
        AudioStageDetector.Detection detection = ResolveClipAudioSource(clips[0].Id, clips[0].AudioSource, detectedAudio, clipAudios, uploadedAudios);
        ltxAudioInjector.TryInject(detection);
    }

    private static IReadOnlyDictionary<int, AudioStageDetector.Detection> BuildPerClipAudioDetections(
        AudioStageDetector detector,
        IReadOnlyList<JsonParser.ClipSpec> clips)
    {
        Dictionary<int, AudioStageDetector.Detection> detections = [];
        foreach (JsonParser.ClipSpec clip in clips)
        {
            AudioStageDetector.Detection detection = detector.DetectAceStepFunTrack(clip.AudioSource);
            if (detection is not null)
            {
                detections[clip.Id] = detection;
            }
        }
        return detections;
    }

    /// <summary>Per-clip <see cref="AudioStageDetector.Detection"/> for upload audio; empty when no clip uses upload (avoids orphan load nodes for native-audio clips).</summary>
    private IReadOnlyDictionary<int, AudioStageDetector.Detection> BuildPerClipUploadDetections(
        JsonParser parser,
        IReadOnlyList<JsonParser.ClipSpec> clips)
    {
        Dictionary<int, AudioStageDetector.Detection> detections = [];
        foreach (JsonParser.ClipSpec clip in clips)
        {
            if (!string.Equals(clip.AudioSource, VideoStagesExtension.AudioSourceUpload, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            AudioFile uploaded = parser.ParseUploadedAudioForClip(clip);
            if (uploaded is null)
            {
                continue;
            }
            string loadNodeId = g.CreateAudioLoadNode(uploaded, "${vsaudioupload}");
            WGNodeData audio = new(
                new JArray(loadNodeId, 0),
                g,
                WGNodeData.DT_AUDIO,
                g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
            detections[clip.Id] = new AudioStageDetector.Detection(audio, loadNodeId, "SwarmLoadAudioB64", loadNodeId, int.MaxValue);
        }
        return detections;
    }

    private static AudioStageDetector.Detection ResolveClipAudioSource(
        int clipId,
        string source,
        AudioStageDetector.Detection detectedAudio,
        IReadOnlyDictionary<int, AudioStageDetector.Detection> clipAudios,
        IReadOnlyDictionary<int, AudioStageDetector.Detection> uploadedAudios)
    {
        if (string.Equals(source, VideoStagesExtension.AudioSourceUpload, StringComparison.OrdinalIgnoreCase))
        {
            return uploadedAudios.TryGetValue(clipId, out AudioStageDetector.Detection detection) ? detection : null;
        }
        if (clipAudios.TryGetValue(clipId, out AudioStageDetector.Detection clipDetection))
        {
            return clipDetection;
        }
        return detectedAudio;
    }

    private bool HasRootVideoModel()
    {
        return GetRootVideoModel() is not null;
    }

    private T2IModel GetRootVideoModel()
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel) && videoModel is not null)
        {
            return videoModel;
        }

        if (g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true)
        {
            return textToVideoModel;
        }

        return null;
    }

    private bool ShouldCaptureRootReferences()
    {
        return HasConfiguredStages();
    }

    private bool HasConfiguredStages()
    {
        T2IParamType type = VideoStagesExtension.VideoStagesJson?.Type;
        if (type is null
            || !g.UserInput.TryGetRaw(type, out object rawValue)
            || rawValue is not string json
            || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        if (json.Trim() == "[]")
        {
            return false;
        }

        return new JsonParser(g).ParseStages().Count > 0;
    }

    private void EnsureFinalStageOutputSaved()
    {
        if (g.UserInput.Get(T2IParamTypes.DoNotSave, false) || g.CurrentMedia is null)
        {
            return;
        }

        if (g.CurrentMedia.Path is not { Count: 2 })
        {
            g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, g.GetStableDynamicID(FinalStageSaveId, 0));
            return;
        }

        if (WorkflowUtils.IsNodeTypeReachableFromOutput(g.Workflow, g.CurrentMedia.Path, NodeTypes.SwarmSaveAnimationWS))
        {
            return;
        }

        g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, g.GetStableDynamicID(FinalStageSaveId, 0));
    }
}
