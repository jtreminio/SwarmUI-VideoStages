namespace VideoStages;

public static class Constants
{
    public static class WorkflowStepPriority
    {
        public const double CoreImageToVideo = 11;

        public const double ControlNetPreprocessors = -5.9;
        public const double EnsureRootVideoStageModel = -4.3;
        public const double CaptureBase = -4.2;
        public const double CaptureRefiner = 5.9;
        public const double SuppressCoreRootVideoStage = 10.95;
        public const double RestoreCoreRootVideoStageModel = 11.05;
        public const double ApplyRootAudioMaskDimensions = 11.4;
        public const double RunConfiguredStages = 11.5;
    }

    public const int SectionID_VideoStages = 48823;
    public const int SectionID_VideoClip = 58823;
    public const double DefaultStageRefStrength = 0.8;
    public const double DefaultStageControlNetStrength = 0.8;
    public const int RootDimensionMin = 256;
    public const int RootDimensionMax = 16384;
    public const string RootDimensionsDescription = """
        These are the starting dimensions for each clip.
        You can apply upscaling to your videos after the first
        stage in any video clip.
    """;
    public const string RootFPSDescription = """
        Frames per second for VideoStages clips.
        Clip duration controls total frame count.
    """;
    public const string DimensionsPresetCustomValue = "custom";
    public static readonly string[] DimensionsPresetOrderedKeys =
    [
        "256x384",
        "384x512",
        "384x640",
        "512x768",
        "512x896",
        "512x1024",
        "768x1024",
        "384x256",
        "512x384",
        "640x384",
        "768x512",
        "896x512",
        "1024x512",
        "1024x768",
    ];
    public static readonly IReadOnlyDictionary<string, string[]> DimensionsPresetMetadataTable =
        new Dictionary<string, string[]>
        {
            ["256x384"] =
            [
                "384x576,1.5",
                "576x864,1.5,1.5",
                "*768x1152,1.5,2",
                "1152x1728,1.5,1.5,2",
            ],
            ["384x512"] =
            [
                "576x768,1.5",
                "864x1152,1.5,1.5",
                "*1152x1536,1.5,2",
                "1728x2304,1.5,1.5,2",
            ],
            ["384x640"] =
            [
                "576x960,1.5",
                "864x1440,1.5,1.5",
                "1152x1920,1.5,2",
                "1728x2880,1.5,1.5,2",
            ],
            ["512x768"] =
            [
                "768x1152,1.5",
                "*1152x1728,1.5,1.5",
                "*1536x2304,1.5,2",
                "2304x3456,1.5,1.5,2",
            ],
            ["512x896"] = ["*1536x2688,1.5,2"],
            ["512x1024"] = ["*1152x2304,1.5,1.5", "*1536x3072,1.5,2"],
            ["768x1024"] = ["*1728x2304,1.5,1.5", "*2304x3072,1.5,2"],
            ["384x256"] =
            [
                "576x384,1.5",
                "864x576,1.5,1.5",
                "*1152x768,1.5,2",
                "1728x1152,1.5,1.5,2",
            ],
            ["512x384"] =
            [
                "768x576,1.5",
                "1152x864,1.5,1.5",
                "*1536x1152,1.5,2",
                "2304x1728,1.5,1.5,2",
            ],
            ["640x384"] =
            [
                "960x576,1.5",
                "1440x864,1.5,1.5",
                "1920x1152,1.5,2",
                "2880x1728,1.5,1.5,2",
            ],
            ["768x512"] =
            [
                "1152x768,1.5",
                "*1728x1152,1.5,1.5",
                "*2304x1536,1.5,2",
                "3456x2304,1.5,1.5,2",
            ],
            ["896x512"] = ["*2688x1536,1.5,2"],
            ["1024x512"] = ["*2304x1152,1.5,1.5", "*3072x1536,1.5,2"],
            ["1024x768"] = ["*2304x1728,1.5,1.5", "*3072x2304,1.5,2"],
        };

    public const string DimensionsPresetDescription = """
        Quick-pick starting resolution for Video Stages clips.
        Choose Custom to set width and height with the sliders below.
    """;

    internal const string ComfyUIFeatureFlag = "comfyui";
    internal const string LtxVideoFeatureFlag = "ltxvideo";
    internal const string LtxVideoNodeUrl = "https://github.com/Lightricks/ComfyUI-LTXVideo";
    public const string AudioSourceNative = "Native";
    public const string AudioSourceUpload = "Upload";
    public const string AudioSourceSwarm = "Swarm Audio";
    public const string ControlNetSourceOne = "ControlNet 1";
    public const string ControlNetSourceTwo = "ControlNet 2";
    public const string ControlNetSourceThree = "ControlNet 3";
}
