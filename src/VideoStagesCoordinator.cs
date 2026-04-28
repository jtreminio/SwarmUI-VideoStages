using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using VideoStages.LTX2;

namespace VideoStages;

internal sealed class VideoStagesCoordinator(
    WorkflowGenerator g,
    JsonParser jsonParser,
    RootVideoStageTakeover rootVideoStageTakeover,
    StageRefStore stageRefStore,
    StageSequenceRunner stageSequenceRunner,
    AudioStageDetector audioStageDetector,
    LtxManager ltxManager)
{
    private const int FinalStageSaveId = 52200;

    public void CaptureBase()
    {
        CaptureIfStagesConfigured(StageRefStore.StageKind.Base);
    }

    public void CaptureRefiner()
    {
        CaptureIfStagesConfigured(StageRefStore.StageKind.Refiner);
    }

    public void RunConfiguredStages()
    {
        if (GetRootVideoModel() is null)
        {
            rootVideoStageTakeover.CleanupSynthesizedRootVideoStageModel();
            return;
        }

        try
        {
            List<JsonParser.ClipSpec> clips = jsonParser.ParseClips();
            List<JsonParser.StageSpec> stages = jsonParser.ParseStages();
            bool rootStageTakeover = rootVideoStageTakeover.ShouldTakeOverRootStage();
            if (stages.Count == 0)
            {
                TryInjectConfiguredAudio(clips);
                return;
            }

            ClipAudioMaps clipAudioMaps = BuildClipAudioMaps(clips);
            if (!rootStageTakeover)
            {
                JsonParser.StageSpec first = stages[0];
                TryInjectResolvedClipAudio(
                    first.ClipId,
                    first.ClipAudioSource,
                    first.ClipLengthFromAudio,
                    clipAudioMaps);
            }

            stageSequenceRunner.Run(
                stages,
                clipAudioMaps.DetectedAudio,
                clipAudioMaps.ClipAudios,
                clipAudioMaps.UploadedAudios,
                rootStageTakeover);
            EnsureFinalStageOutputSaved();
        }
        finally
        {
            rootVideoStageTakeover.CleanupSynthesizedRootVideoStageModel();
        }
    }

    private void CaptureIfStagesConfigured(StageRefStore.StageKind kind)
    {
        if (!HasConfiguredStages())
        {
            return;
        }

        stageRefStore.Capture(kind);
    }

    private void TryInjectConfiguredAudio(List<JsonParser.ClipSpec> clips)
    {
        if (clips.Count == 0)
        {
            ltxManager.TryInjectAudio(audioStageDetector.Detect());
            return;
        }

        ClipAudioMaps clipAudioMaps = BuildClipAudioMaps(clips);
        JsonParser.ClipSpec first = clips[0];
        TryInjectResolvedClipAudio(first.Id, first.AudioSource, first.ClipLengthFromAudio, clipAudioMaps);
    }

    private readonly record struct ClipAudioMaps(
        AudioStageDetector.Detection DetectedAudio,
        IReadOnlyDictionary<int, AudioStageDetector.Detection> ClipAudios,
        IReadOnlyDictionary<int, AudioStageDetector.Detection> UploadedAudios);

    private ClipAudioMaps BuildClipAudioMaps(IReadOnlyList<JsonParser.ClipSpec> clips)
    {
        AudioStageDetector.Detection detectedAudio = audioStageDetector.Detect();
        IReadOnlyDictionary<int, AudioStageDetector.Detection> clipAudios =
            BuildPerClipAudioDetections(audioStageDetector, clips);
        IReadOnlyDictionary<int, AudioStageDetector.Detection> uploadedAudios =
            BuildPerClipUploadDetections(clips);
        AceStepFunAudioSavePruner.Apply(g, clips);
        return new ClipAudioMaps(detectedAudio, clipAudios, uploadedAudios);
    }

    private void TryInjectResolvedClipAudio(
        int clipId,
        string audioSource,
        bool clipLengthFromAudio,
        ClipAudioMaps maps)
    {
        AudioStageDetector.Detection detection = ClipAudioWorkflowHelper.ResolveClipAudioDetection(
            clipId,
            audioSource,
            maps.DetectedAudio,
            maps.ClipAudios,
            maps.UploadedAudios,
            suppressNativeFallback: false,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.CoordinatorField);
        _ = ltxManager.TryInjectAudio(
            detection,
            ClipAudioWorkflowHelper.ShouldMatchVideoLengthForTryInjectAudio(
                audioSource,
                clipLengthFromAudio,
                restrictLengthMatchToUploadOrAce: false));
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

    private IReadOnlyDictionary<int, AudioStageDetector.Detection> BuildPerClipUploadDetections(
        IReadOnlyList<JsonParser.ClipSpec> clips)
    {
        Dictionary<int, AudioStageDetector.Detection> detections = [];
        foreach (JsonParser.ClipSpec clip in clips)
        {
            if (!StringUtils.Equals(clip.AudioSource, Constants.AudioSourceUpload))
            {
                continue;
            }
            AudioFile uploaded = jsonParser.ParseUploadedAudioForClip(clip);
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
            detections[clip.Id] = new AudioStageDetector.Detection(
                audio,
                loadNodeId,
                "SwarmLoadAudioB64",
                loadNodeId,
                int.MaxValue);
        }
        return detections;
    }

    private T2IModel GetRootVideoModel()
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel)
            && videoModel is not null)
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

        ReadOnlySpan<char> trimmed = json.AsSpan().Trim();
        if (trimmed.Length == 2 && trimmed[0] == '[' && trimmed[1] == ']')
        {
            return false;
        }

        return jsonParser.ParseStages().Count > 0;
    }

    private void EnsureFinalStageOutputSaved()
    {
        if (g.UserInput.Get(T2IParamTypes.DoNotSave, false) || g.CurrentMedia is null)
        {
            return;
        }

        string saveId = g.GetStableDynamicID(FinalStageSaveId, 0);
        if (g.CurrentMedia.Path is { Count: 2 }
            && WorkflowUtils.IsNodeTypeReachableFromOutput(
                g.Workflow,
                g.CurrentMedia.Path,
                NodeTypes.SwarmSaveAnimationWS))
        {
            return;
        }

        g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, saveId);
    }
}
