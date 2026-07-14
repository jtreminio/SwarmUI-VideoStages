namespace VideoStages;

public static class Constants
{
    public static class WorkflowStepPriority
    {
        public const double CoreImageToVideo = 11;
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
    public const double DefaultStageRefStrength = 0.8;
    public const double DefaultStageControlNetStrength = 0.8;

    internal const string ComfyUIFeatureFlag = "comfyui";
    internal const string LtxVideoFeatureFlag = "ltxvideo";
    internal const string LtxVideoNodeUrl = "https://github.com/Lightricks/ComfyUI-LTXVideo";
    public const string AudioSourceNative = "Native";
    public const string AudioSourceUpload = "Upload";
    public const string AudioSourceSwarm = "Swarm Audio";
    public const string AudioSourceControlNet = "ControlNet";
    public const string ControlNetSourceOne = "ControlNet 1";
    public const string ControlNetSourceTwo = "ControlNet 2";
    public const string ControlNetSourceThree = "ControlNet 3";

    // Outgoing boundary between clip N and N+1; mirrors the frontend BoundaryOut union.
    // "continue" = generation-time continuity: the next clip is generated from this clip's final frame
    // (first-frame guide at strength 1) and the merge collapses the duplicated seam frame via a 1-frame
    // overlap. Degrades to "cut" when the next clip can't consume it (non-LTX-2 first stage, explicit
    // first-frame ref, or unknown frame count).
    public const string BoundaryOutCut = "cut";
    public const string BoundaryOutContinue = "continue";
    public const string BoundaryOutCrossfade = "crossfade";
}
