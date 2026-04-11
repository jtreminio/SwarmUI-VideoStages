using System.IO;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

public class VideoStagesExtension : Extension
{
    public const int SectionID_VideoStages = 48823;
    public const double DefaultLTXVImgToVideoInplaceStrength = 0.8;

    public static int SectionIdForStage(int stageIndex) => SectionID_VideoStages + 1 + stageIndex;
    public static T2IRegisteredParam<bool> ConnectAudioToVideo;
    public static T2IRegisteredParam<bool> EnableVideoStages;
    public static T2IRegisteredParam<int> RootStageWidth;
    public static T2IRegisteredParam<int> RootStageHeight;
    public static T2IRegisteredParam<string> RootGuideImageReference;
    public static T2IRegisteredParam<string> VideoStagesJson;
    public static T2IRegisteredParam<double> LTXVImgToVideoInplaceStrength;

    public override void OnPreInit()
    {
        StyleSheetFiles.Add("Assets/video-stages.css");
        ScriptFiles.Add("Assets/video-stages.js");
    }

    public override void OnInit()
    {
        Logs.Info("VideoStages Extension initializing...");
        RegisterParameters();
        RegisterComfyNodes();
        RootVideoStageResizer.EnsureRegistered();
        WorkflowGenerator.AddStep(g => new VideoStagesCoordinator(g).CaptureBase(), -4.2);
        WorkflowGenerator.AddStep(g => new VideoStagesCoordinator(g).CaptureRefiner(), 5.9);
        WorkflowGenerator.AddStep(RootVideoStageResizer.ApplyRootAudioMaskDimensionsAfterNativeVideo, 11.4);
        WorkflowGenerator.AddStep(g => new VideoStagesCoordinator(g).RunConfiguredStages(), 11.5);
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

        ConnectAudioToVideo = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "Connect Audio to Video",
            Description: "Connect detected audio (if any) to your videos.",
            Default: "true",
            Group: VideoStagesGroup,
            OrderPriority: OrderPriority,
            FeatureFlag: "comfyui"
        ));
        OrderPriority += 1;

        RootStageWidth = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Root Width",
            Description: "Optional width override for the first additional video stage input. When both Root Width and Root Height are set, the first stage input is scaled before extracting frames.",
            Default: "512",
            Min: 256,
            ViewMin: 256,
            Max: 16384,
            ViewMax: 2048,
            Step: 32,
            Toggleable: true,
            Group: VideoStagesGroup,
            OrderPriority: OrderPriority,
            FeatureFlag: "comfyui",
            DoNotPreview: true
        ));
        OrderPriority += 1;

        RootStageHeight = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Root Height",
            Description: "Optional height override for the first additional video stage input. When both Root Width and Root Height are set, the first stage input is scaled before extracting frames.",
            Default: "512",
            Min: 256,
            ViewMin: 256,
            Max: 16384,
            ViewMax: 2048,
            Step: 32,
            Toggleable: true,
            Group: VideoStagesGroup,
            OrderPriority: OrderPriority,
            FeatureFlag: "comfyui",
            DoNotPreview: true
        ));
        OrderPriority += 1;

        RootGuideImageReference = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Guide Image Reference",
            Description: "Which earlier image should be used as the root video guide image before the first video pass. 'Default' keeps the current root image behavior; 'Base', 'Refiner', and 'editN' options can be selected from the frontend when available.",
            Default: "Default",
            GetValues: (_) => ["Default", "Base", "Refiner"],
            Group: VideoStagesGroup,
            OrderPriority: OrderPriority,
            FeatureFlag: "comfyui",
            DoNotPreview: true
        ));
        OrderPriority += 1;

        EnableVideoStages = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "Enable additional Video Stages",
            Description: "Enable additional video stages.",
            Default: "false",
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
