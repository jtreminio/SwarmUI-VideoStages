using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using VideoStages.LTX2;

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
            return;
        }

        TryInjectDetectedAudio();

        List<JsonParser.StageSpec> stages = HasConfiguredStages()
            ? new JsonParser(g).ParseStages()
            : [];
        if (stages.Count == 0)
        {
            return;
        }

        new StageSequenceRunner(g, new StageRefStore(g), stages).Run();
        EnsureFinalStageOutputSaved();
    }

    private void TryInjectDetectedAudio()
    {
        if (!g.IsLTXV2() || g.CurrentAudioVae is null)
        {
            return;
        }

        string source = $"{g.UserInput.Get(VideoStagesExtension.AudioSource, VideoStagesExtension.AudioSourceNative)}".Trim();
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        AudioStageDetector.Detection detection = string.Equals(source, VideoStagesExtension.AudioSourceUpload, StringComparison.Ordinal)
            ? BuildUploadDetection()
            : new AudioStageDetector(g).Detect();
        if (detection is null)
        {
            return;
        }

        _ = new AudioInjector(g).TryInject(detection);
    }

    private AudioStageDetector.Detection BuildUploadDetection()
    {
        if (!g.UserInput.TryGet(VideoStagesExtension.AudioUpload, out AudioFile uploaded) || uploaded is null)
        {
            return null;
        }

        string loadNodeId = g.CreateAudioLoadNode(uploaded, "${vsaudioupload}");
        WGNodeData audio = new(
            new JArray(loadNodeId, 0),
            g,
            WGNodeData.DT_AUDIO,
            g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
        return new AudioStageDetector.Detection(audio, loadNodeId, "SwarmLoadAudioB64", loadNodeId, int.MaxValue);
    }

    private bool HasRootVideoModel()
    {
        return GetRootVideoModel() is not null;
    }

    private bool HasNativeRootVideoModel()
    {
        return g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel imageToVideoModel)
            && imageToVideoModel is not null;
    }

    private T2IModel GetRootVideoModel()
    {
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel imageToVideoModel) && imageToVideoModel is not null)
        {
            return imageToVideoModel;
        }

        if (g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true)
        {
            return textToVideoModel;
        }

        return null;
    }

    private bool IsVideoStagesEnabledForVideo()
    {
        if (!g.UserInput.Get(VideoStagesExtension.EnableVideoStages, false))
        {
            return false;
        }

        return HasRootVideoModel();
    }

    private bool ShouldCaptureRootReferences()
    {
        return HasConfiguredStages() || NeedsRootGuideReferenceCapture();
    }

    private bool NeedsRootGuideReferenceCapture()
    {
        if (!HasNativeRootVideoModel()
            || !g.UserInput.TryGet(VideoStagesExtension.RootGuideImageReference, out string guideReference))
        {
            return false;
        }

        string compact = ImageReferenceSyntax.Compact(guideReference);
        return string.Equals(compact, "Base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "Refiner", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasConfiguredStages()
    {
        if (!IsVideoStagesEnabledForVideo())
        {
            return false;
        }

        T2IParamType type = VideoStagesExtension.VideoStagesJson?.Type;
        if (type is null
            || !g.UserInput.TryGetRaw(type, out object rawValue)
            || rawValue is not string json
            || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        return json.Trim() != "[]";
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
