using System.IO;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.LTX2;

namespace VideoStages;

public class VideoStagesExtension : Extension
{
    public const int SectionID_VideoStages = 48823;
    public const int SectionID_VideoClip = 58823;
    public const double DefaultStageRefStrength = 0.8;
    public const int RootDimensionMin = 256;
    public const int RootDimensionMax = 16384;
    public const string RootDimensionsDescription = "These are the starting dimensions for each clip. The first stage in each clip cannot apply per-stage upscale; use later stages or the main workflow for scaling if needed.";
    private const string ComfyUIFeatureFlag = "comfyui";
    public const string AudioSourceNative = "Native";
    public const string AudioSourceUpload = "Upload";
    public const string AudioSourceSwarm = "Swarm Audio";

    public static int SectionIdForStage(int stageIndex) => SectionID_VideoStages + 1 + stageIndex;
    public static int VideoClipSectionIdForClip(int clipIndex) => SectionID_VideoClip + 1 + clipIndex;
    public static T2IRegisteredParam<int> RootWidth;
    public static T2IRegisteredParam<int> RootHeight;
    public static T2IRegisteredParam<int> RootFPS;
    public static T2IRegisteredParam<string> VideoStagesJson;
    public static WorkflowGenerator.WorkflowGenStep CoreImageToVideoStep;

    public override void OnPreInit()
    {
        PromptRegion.RegisterCustomPrefix("videoclip");
        T2IPromptHandling.PromptTagBasicProcessors["videoclip"] = (data, context) =>
        {
            if (int.TryParse(context.PreData, out int clipIndex) && clipIndex >= 0)
            {
                context.SectionID = VideoClipSectionIdForClip(clipIndex);
            }
            else
            {
                context.SectionID = SectionID_VideoClip;
            }
            return $"<videoclip//cid={context.SectionID}>";
        };
        T2IPromptHandling.PromptTagLengthEstimators["videoclip"] = (data, context) => "<break>";

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
        WorkflowGenerator.AddStep(RootVideoStageTakeover.EnsureRootVideoStageModel, -4.3);
        WorkflowGenerator.AddStep(g => new VideoStagesCoordinator(g).CaptureBase(), -4.2);
        WorkflowGenerator.AddStep(g => new VideoStagesCoordinator(g).CaptureRefiner(), 5.9);
        WorkflowGenerator.AddStep(RootVideoStageTakeover.SuppressCoreRootVideoStage, 10.95);
        WorkflowGenerator.AddStep(RootVideoStageTakeover.RestoreCoreRootVideoStageModel, 11.05);
        WorkflowGenerator.AddStep(LtxAudioMaskResizer.ApplyRootAudioMaskDimensionsAfterNativeVideo, 11.4);
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

        int OrderPriority = 0;

        RootWidth = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Video Stages Width",
            Description: RootDimensionsDescription,
            Default: "1024",
            Min: 256,
            Max: RootDimensionMax,
            ViewMin: RootDimensionMin,
            ViewMax: 4096,
            Step: 32,
            ViewType: ParamViewType.POT_SLIDER,
            HideFromMetadata: false,
            DoNotPreview: true,
            Toggleable: true,
            Group: VideoStagesGroup,
            OrderPriority: OrderPriority,
            FeatureFlag: ComfyUIFeatureFlag
        ));
        OrderPriority += 1;

        RootHeight = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Video Stages Height",
            Description: RootDimensionsDescription,
            Default: "1024",
            Min: 256,
            Max: RootDimensionMax,
            ViewMin: RootDimensionMin,
            ViewMax: 4096,
            Step: 32,
            ViewType: ParamViewType.POT_SLIDER,
            HideFromMetadata: false,
            DoNotPreview: true,
            Toggleable: true,
            Group: VideoStagesGroup,
            OrderPriority: OrderPriority,
            FeatureFlag: ComfyUIFeatureFlag
        ));
        OrderPriority += 1;

        RootFPS = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Video Stages FPS",
            Description: "Frames per second for VideoStages clips. Clip duration controls total frame count.",
            Default: "24",
            Min: 4,
            Max: 60,
            ViewMin: 4,
            ViewMax: 60,
            Step: 4,
            ViewType: ParamViewType.SLIDER,
            HideFromMetadata: false,
            DoNotPreview: true,
            Toggleable: true,
            Group: VideoStagesGroup,
            OrderPriority: OrderPriority,
            FeatureFlag: ComfyUIFeatureFlag
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
            FeatureFlag: ComfyUIFeatureFlag
        ));
    }
}
