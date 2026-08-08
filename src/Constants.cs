namespace VideoStages;

public static class Constants
{
    public static class WorkflowStepPriority
    {
        public const double CoreImageToVideo = 11;
        public const double PreflightRequest = -6;
        public const double ControlNetPreprocessors = -5.9;
        public const double CaptureBase = -4.2;
        public const double CaptureRefiner = 5.89;
        public const double CapturePreCoreVideoMedia = 10.95;
        public const double DropCoreImageToVideoOutput = 11.05;
        public const double ApplyRootAudioMaskDimensions = 11.4;
        public const double RunConfiguredStages = 11.5;
    }

    public const int SectionID_VideoStages = 48823;
    public const int SectionID_VideoClip = 58823;
    public const int SectionID_VideoClipUnmatched = 68823;
    public const int StagedNodeIdReservationFloor = 1_000_000;

    public const string SweptHostRootNodesKey = "videostages.host-root.swept";
    public const double DefaultStageRefStrength = 0.8;
    public const double DefaultStageControlNetStrength = 0.8;

    internal const string ComfyUIFeatureFlag = "comfyui";

    // The wire spellings below are the only ones there are: ArchitectureFeatureVocabulary
    // projects them into frontend/architectures/generatedFeatures.ts, so the frontend unions
    // follow an edit here without being touched.
    public const string IcLoraControlNone = "none";
    public const string IcLoraControlCanny = "canny";
    public const string IcLoraControlDepth = "depth";
    public const string IcLoraControlNormal = "normal";
    public const string ReferenceFramingCrop = "crop";
    public const string ReferenceFramingStretch = "stretch";
    public const string ReferenceFramingFit = "fit";
    public const string ReferenceFramingFitGreen = "fit-green";
    public const string BoundaryOutCut = "cut";
    public const string BoundaryOutContinue = "continue";
    public const string BoundaryOutCrossfade = "crossfade";
}
