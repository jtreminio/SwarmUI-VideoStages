using System.IO;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using VideoStages.Architectures;

namespace VideoStages;

public class VideoStagesExtension : Extension
{
    public static int SectionIdForStage(int stageIndex) => Constants.SectionID_VideoStages + 1 + stageIndex;
    public static int SectionIdForClip(int clipIndex) => Constants.SectionID_VideoClip + 1 + clipIndex;
    public static T2IRegisteredParam<bool> Enabled;
    public static T2IRegisteredParam<string> Data;
    public static T2IRegisteredParam<Image> RefineSourceVideo;
    public static T2IRegisteredParam<int> RefineSkipStages;
    public static WorkflowGenerator.WorkflowGenStep CoreImageToVideoStep;

    public override void OnPreInit()
    {
        PromptRegion.RegisterCustomPrefix("videoclip");
        T2IPromptHandling.PromptTagBasicProcessors["videoclip"] = PromptParser.ProcessVideoClipTag;
        T2IPromptHandling.PromptTagBasicProcessors["videostages"] = PromptParser.ProcessVideoStagesTag;
        T2IPromptHandling.PromptTagLengthEstimators["videoclip"] = (_, _) => "<break>";
        T2IPromptHandling.PromptTagLengthEstimators["videostages"] = (_, _) => "";
        StyleSheetFiles.Add("Assets/video-stages.css");
        ScriptFiles.Add("Assets/video-stages.js");
    }

    public override void OnInit()
    {
        Logs.Info("VideoStages Extension initializing...");
        ComfyTyped.Generated.NodeRegistrations.EnsureRegistered();
        VideoStages.Generated.NodeRegistrations.EnsureRegistered();
        VideoArchitectureManifest.RegisterProductionDependencies();
        RegisterParameters();
        RegisterComfyNodes();
        VideoStagesApi.Register();
        AttachPromptMetadataRestorer("prompt");
        AttachPromptMetadataRestorer("negativeprompt");
        CoreImageToVideoStep = WorkflowGenerator.Steps.FirstOrDefault(
            step => step.Priority == Constants.WorkflowStepPriority.CoreImageToVideo);
        AltImageToVideoScope.RegisterDispatcher();
        VideoArchitectureManifest.RegisterProductionHostHandlers();

        WorkflowGenerator.AddStep(
            Runner.CaptureCoreVideoControlNetPreprocessors,
            Constants.WorkflowStepPriority.ControlNetPreprocessors);
        WorkflowGenerator.AddStep(
            Runner.CaptureBase,
            Constants.WorkflowStepPriority.CaptureBase);
        WorkflowGenerator.AddStep(
            Runner.CaptureRefiner,
            Constants.WorkflowStepPriority.CaptureRefiner);
        WorkflowGenerator.AddStep(
            Runner.CapturePreCoreVideoMedia,
            Constants.WorkflowStepPriority.CapturePreCoreVideoMedia);
        WorkflowGenerator.AddStep(
            Runner.DropCoreImageToVideoOutput,
            Constants.WorkflowStepPriority.DropCoreImageToVideoOutput);
        WorkflowGenerator.AddStep(
            Runner.ApplyRootAudioMaskDimensionsAfterNativeVideo,
            Constants.WorkflowStepPriority.ApplyRootAudioMaskDimensions);
        WorkflowGenerator.AddStep(
            Runner.RunConfiguredStages,
            Constants.WorkflowStepPriority.RunConfiguredStages);
    }

    private static void RegisterParameters()
    {
        T2IParamGroup VideoStagesGroup = new(
            Name: "VideoStages",
            Description: "Adds extra video stages, and connect audio to your videos.",
            Toggles: true,
            Open: false,
            OrderPriority: -2.9
        );

        Enabled = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "Video Stages Enabled",
            Description: "Internal gate flag for the VideoStages group.",
            Default: "true",
            VisibleNormally: false,
            Toggleable: false,
            DoNotPreview: true,
            HideFromMetadata: true,
            Group: VideoStagesGroup,
            FeatureFlag: Constants.ComfyUIFeatureFlag
        ));

        Data = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Video Stages",
            Description: "Internal",
            Default: "",
            VisibleNormally: false,
            DoNotPreview: true,
            Group: VideoStagesGroup,
            FeatureFlag: Constants.ComfyUIFeatureFlag,
            MetadataFormat: MetadataSanitizer.StripUploadDataFromJsonParameter
        ));

        RefineSourceVideo = T2IParamTypes.Register<Image>(new T2IParamType(
            Name: "Video Stages Refine Source Video",
            Description: "",
            Default: null,
            VisibleNormally: false,
            DoNotPreview: true,
            HideFromMetadata: true,
            Group: VideoStagesGroup,
            FeatureFlag: Constants.ComfyUIFeatureFlag
        ));

        RefineSkipStages = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Video Stages Refine Skip Stages",
            Description: "",
            Default: "0",
            Min: 0,
            Max: 99,
            VisibleNormally: false,
            DoNotPreview: true,
            HideFromMetadata: true,
            Group: VideoStagesGroup,
            FeatureFlag: Constants.ComfyUIFeatureFlag
        ));
    }

    /// <summary>Metadata stores the PROCESSED prompt, where videoclip tags have been rewritten into internal
    /// PromptRegion markers (<c>&lt;videoclip:w|0|0.5|4//cid=N&gt;</c>). Attach a MetadataFormat to the core
    /// prompt params so saved metadata shows the tags as the user typed them.</summary>
    private static void AttachPromptMetadataRestorer(string paramId)
    {
        if (!T2IParamTypes.Types.TryGetValue(paramId, out T2IParamType type))
        {
            return;
        }
        Func<string, string> existing = type.MetadataFormat;
        T2IParamTypes.Types[paramId] = type with
        {
            MetadataFormat = existing is null
                ? PromptParser.RestoreTagsForMetadata
                : val => existing(PromptParser.RestoreTagsForMetadata(val)),
        };
    }

    private void RegisterComfyNodes()
    {
        string rootPath = string.IsNullOrWhiteSpace(FilePath) ? "src/Extensions/SwarmUI-VideoStages" : FilePath;
        string nodeFolder = Path.GetFullPath(Path.Join(rootPath, "comfy_node"));
        ComfyUISelfStartBackend.CustomNodePaths.Add(nodeFolder);
        Logs.Init($"VideoStages: added {nodeFolder} to ComfyUI CustomNodePaths");
    }
}
