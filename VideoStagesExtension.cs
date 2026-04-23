using System.IO;
using System.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

public class VideoStagesExtension : Extension
{
    public const int SectionID_VideoStages = 48823;
    public const double DefaultLTXVImgToVideoInplaceStrength = 0.8;
    public const int RootDimensionMin = 256;
    public const int RootDimensionMax = 16384;
    public const string RootDimensionsDescription = "These are the starting dimensions for each clip. You can upscale later in any stage.";

    public const string AudioSourceNative = "Native";
    public const string AudioSourceUpload = "Upload";
    public const string AudioSourceSwarm = "Swarm Audio";

    public static int SectionIdForStage(int stageIndex) => SectionID_VideoStages + 1 + stageIndex;
    public static T2IRegisteredParam<int> RootWidth;
    public static T2IRegisteredParam<int> RootHeight;
    public static T2IRegisteredParam<string> VideoStagesJson;
    public static T2IRegisteredParam<double> LTXVImgToVideoInplaceStrength;
    public static WorkflowGenerator.WorkflowGenStep CoreImageToVideoStep;

    public override void OnPreInit()
    {
        StyleSheetFiles.Add("Assets/video-stages.css");
        ScriptFiles.Add("Assets/video-stages.js");
    }

    public override void OnInit()
    {
        Logs.Info("VideoStages Extension initializing...");
        CaptureCoreImageToVideoStep();
        RegisterParameters();
        RegisterComfyNodes();
        RootVideoStageResizer.EnsureRegistered();
        WorkflowGenerator.AddStep(g => new VideoStagesCoordinator(g).CaptureBase(), -4.2);
        WorkflowGenerator.AddStep(g => new VideoStagesCoordinator(g).CaptureRefiner(), 5.9);
        WorkflowGenerator.AddStep(RootVideoStageTakeover.SuppressCoreRootVideoStage, 10.95);
        WorkflowGenerator.AddStep(RootVideoStageTakeover.RestoreCoreRootVideoStageModel, 11.05);
        WorkflowGenerator.AddStep(RootVideoStageResizer.ApplyRootAudioMaskDimensionsAfterNativeVideo, 11.4);
        WorkflowGenerator.AddStep(g => new VideoStagesCoordinator(g).RunConfiguredStages(), 11.5);
    }

    private static void CaptureCoreImageToVideoStep()
    {
        if (CoreImageToVideoStep is not null)
        {
            return;
        }
        CoreImageToVideoStep = WorkflowGenerator.Steps.FirstOrDefault(step => step.Priority == 11);
    }

    private void RegisterComfyNodes()
    {
        string rootPath = string.IsNullOrWhiteSpace(FilePath) ? "src/Extensions/SwarmUI-VideoStages" : FilePath;
        string nodeFolder = Path.GetFullPath(Path.Join(rootPath, "comfy_node"));
        if (!Directory.Exists(nodeFolder))
        {
            return;
        }
        if (ComfyUISelfStartBackend.CustomNodePaths.Contains(nodeFolder))
        {
            return;
        }

        ComfyUISelfStartBackend.CustomNodePaths.Add(nodeFolder);
        Logs.Init($"VideoStages: added {nodeFolder} to ComfyUI CustomNodePaths");
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

        double OrderPriority = 0;

        // Min: 0 lets us use 0 as a sentinel for "no custom value yet" so the
        // frontend can mirror the user's currently-selected core Width/Height
        // into our slider on each panel build until the user moves it.
        RootWidth = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Video Stages Width",
            Description: RootDimensionsDescription,
            Default: "0",
            Min: 0,
            Max: RootDimensionMax,
            ViewMin: RootDimensionMin,
            ViewMax: 4096,
            Step: 32,
            ViewType: ParamViewType.POT_SLIDER,
            HideFromMetadata: false,
            DoNotPreview: true,
            Group: VideoStagesGroup,
            OrderPriority: OrderPriority,
            FeatureFlag: "comfyui"
        ));
        OrderPriority += 1;

        RootHeight = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Video Stages Height",
            Description: RootDimensionsDescription,
            Default: "0",
            Min: 0,
            Max: RootDimensionMax,
            ViewMin: RootDimensionMin,
            ViewMax: 4096,
            Step: 32,
            ViewType: ParamViewType.POT_SLIDER,
            HideFromMetadata: false,
            DoNotPreview: true,
            Group: VideoStagesGroup,
            OrderPriority: OrderPriority,
            FeatureFlag: "comfyui"
        ));
        OrderPriority += 1;

        VideoStagesJson = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Video Stages",
            Description: "",
            Default: "",
            VisibleNormally: false,
            IsAdvanced: false,
            HideFromMetadata: false,
            DoNotPreview: true,
            Group: VideoStagesGroup,
            FeatureFlag: "comfyui"
        ));

        LTXVImgToVideoInplaceStrength = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "Video Stages LTXVImgToVideoInplaceStrength",
            Description: ".",
            Default: $"{DefaultLTXVImgToVideoInplaceStrength}",
            Min: 0.1,
            Max: 1,
            VisibleNormally: false,
            IsAdvanced: true,
            HideFromMetadata: false,
            DoNotPreview: true,
            Group: VideoStagesGroup,
            FeatureFlag: "comfyui"
        ));
    }
}
