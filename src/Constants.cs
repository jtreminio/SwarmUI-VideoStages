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

    /// <summary>Host root nodes publishing the timeline still had to delete, as <c>id=class,</c>
    /// entries and possibly empty. Adoption is meant to leave it empty; anything in it is a node
    /// the timeline built beside rather than on. Covers the two host-root deletions
    /// <c>RootRuntimeSession</c> performs and no other, so a deletion reaching a host node by some
    /// other path would go unrecorded. Renumbering a node is not a deletion and is not counted.</summary>
    public const string SweptHostRootNodesKey = "videostages.host-root.swept";
    public const double DefaultStageRefStrength = 0.8;
    public const double DefaultStageControlNetStrength = 0.8;

    internal const string ComfyUIFeatureFlag = "comfyui";
    internal const string IcLoraLegacySourceStageInput = "Stage Input";
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
