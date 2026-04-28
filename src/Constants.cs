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
    internal const string ComfyUIFeatureFlag = "comfyui";
    public const string AudioSourceNative = "Native";
    public const string AudioSourceUpload = "Upload";
    public const string AudioSourceSwarm = "Swarm Audio";
    public const string ControlNetSourceOne = "ControlNet 1";
    public const string ControlNetSourceTwo = "ControlNet 2";
    public const string ControlNetSourceThree = "ControlNet 3";
}
