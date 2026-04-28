using System.IO;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

public class VideoStagesExtension : Extension
{
    public static int SectionIdForStage(int stageIndex) => Constants.SectionID_VideoStages + 1 + stageIndex;
    public static int SectionIdForClip(int clipIndex) => Constants.SectionID_VideoClip + 1 + clipIndex;
    public static T2IRegisteredParam<int> RootWidth;
    public static T2IRegisteredParam<int> RootHeight;
    public static T2IRegisteredParam<int> RootFPS;
    public static T2IRegisteredParam<string> VideoStagesJson;
    public static WorkflowGenerator.WorkflowGenStep CoreImageToVideoStep;

    public override void OnPreInit()
    {
        PromptRegion.RegisterCustomPrefix("videoclip");
        T2IPromptHandling.PromptTagBasicProcessors["videoclip"] = (_, context) =>
        {
            if (int.TryParse(context.PreData, out int clipIndex) && clipIndex >= 0)
            {
                context.SectionID = SectionIdForClip(clipIndex);
            }
            else
            {
                context.SectionID = Constants.SectionID_VideoClip;
            }
            return $"<videoclip//cid={context.SectionID}>";
        };
        T2IPromptHandling.PromptTagLengthEstimators["videoclip"] = (_, _) => "<break>";

        StyleSheetFiles.Add("Assets/video-stages.css");
        ScriptFiles.Add("Assets/video-stages.js");
    }

    public override void OnInit()
    {
        Logs.Info("VideoStages Extension initializing...");
        RegisterParameters();
        RegisterComfyNodes();
        CoreImageToVideoStep = WorkflowGenerator.Steps.FirstOrDefault(
            step => step.Priority == Constants.WorkflowStepPriority.CoreImageToVideo);
        RootVideoStageResizer.EnsureRegistered();

        WorkflowGenerator.AddStep(
            g => new Runner(g).CaptureCoreVideoControlNetPreprocessors(),
            Constants.WorkflowStepPriority.ControlNetPreprocessors);
        WorkflowGenerator.AddStep(
            g => new Runner(g).EnsureRootVideoStageModel(),
            Constants.WorkflowStepPriority.EnsureRootVideoStageModel);
        WorkflowGenerator.AddStep(
            g => new Runner(g).CaptureBase(),
            Constants.WorkflowStepPriority.CaptureBase);
        WorkflowGenerator.AddStep(
            g => new Runner(g).CaptureRefiner(),
            Constants.WorkflowStepPriority.CaptureRefiner);
        WorkflowGenerator.AddStep(
            g => new Runner(g).SuppressCoreRootVideoStage(),
            Constants.WorkflowStepPriority.SuppressCoreRootVideoStage);
        WorkflowGenerator.AddStep(
            g => new Runner(g).RestoreCoreRootVideoStageModel(),
            Constants.WorkflowStepPriority.RestoreCoreRootVideoStageModel);
        WorkflowGenerator.AddStep(
            g => new Runner(g).ApplyRootAudioMaskDimensionsAfterNativeVideo(),
            Constants.WorkflowStepPriority.ApplyRootAudioMaskDimensions);
        WorkflowGenerator.AddStep(
            g => new Runner(g).RunConfiguredStages(),
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

        int orderPriority = 0;

        RootWidth = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Video Stages Width",
            Description: Constants.RootDimensionsDescription,
            Default: "1024",
            Min: Constants.RootDimensionMin,
            Max: Constants.RootDimensionMax,
            ViewMin: Constants.RootDimensionMin,
            ViewMax: 4096,
            Step: 32,
            ViewType: ParamViewType.POT_SLIDER,
            DoNotPreview: true,
            Toggleable: true,
            Group: VideoStagesGroup,
            OrderPriority: orderPriority++,
            FeatureFlag: Constants.ComfyUIFeatureFlag
        ));

        RootHeight = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Video Stages Height",
            Description: Constants.RootDimensionsDescription,
            Default: "1024",
            Min: Constants.RootDimensionMin,
            Max: Constants.RootDimensionMax,
            ViewMin: Constants.RootDimensionMin,
            ViewMax: 4096,
            Step: 32,
            ViewType: ParamViewType.POT_SLIDER,
            DoNotPreview: true,
            Toggleable: true,
            Group: VideoStagesGroup,
            OrderPriority: orderPriority++,
            FeatureFlag: Constants.ComfyUIFeatureFlag
        ));

        RootFPS = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Video Stages FPS",
            Description: Constants.RootFPSDescription,
            Default: "24",
            Min: 4,
            Max: 60,
            ViewMin: 4,
            ViewMax: 60,
            Step: 4,
            ViewType: ParamViewType.SLIDER,
            DoNotPreview: true,
            Toggleable: true,
            Group: VideoStagesGroup,
            OrderPriority: orderPriority++,
            FeatureFlag: Constants.ComfyUIFeatureFlag
        ));

        VideoStagesJson = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Video Stages",
            Description: "",
            Default: "",
            VisibleNormally: false,
            DoNotPreview: true,
            Group: VideoStagesGroup,
            FeatureFlag: Constants.ComfyUIFeatureFlag
        ));
    }

    private void RegisterComfyNodes()
    {
        string rootPath = string.IsNullOrWhiteSpace(FilePath) ? "src/Extensions/SwarmUI-VideoStages" : FilePath;
        string nodeFolder = Path.GetFullPath(Path.Join(rootPath, "comfy_node"));
        ComfyUISelfStartBackend.CustomNodePaths.Add(nodeFolder);
        Logs.Init($"VideoStages: added {nodeFolder} to ComfyUI CustomNodePaths");
    }
}
