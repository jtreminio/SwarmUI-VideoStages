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
    public const string AudioSourceNative = "Native";
    public const string AudioSourceUpload = "Upload";
    public const string AudioSourceControlNet = "ControlNet";
    // The uploaded audio is a speaker-identity sample, not a locked timeline track.
    public const string ControlNetSourceOne = "ControlNet 1";
    public const string ControlNetSourceTwo = "ControlNet 2";
    public const string ControlNetSourceThree = "ControlNet 3";

    // IC-LoRA drive-video sources: an embedded per-entry upload, the frames entering the entry's
    // target stage (= previous stage's output; requires Stage >= 1), or one of the captured core
    // "ControlNet N" branches above.
    public const string IcLoraSourceUpload = "Upload";
    public const string IcLoraSourceStageInput = "Stage Input";
    // IC-LoRA control-signal renderings of the drive video. "none" feeds the raw frames (the common
    // case for v2v effect/restoration LoRAs); the rest target Union-Control-style structural LoRAs.
    public const string IcLoraControlNone = "none";
    public const string IcLoraControlCanny = "canny";
    public const string IcLoraControlDepth = "depth";
    public const string IcLoraControlNormal = "normal";
    // Outgoing boundary between clip N and N+1; mirrors the frontend BoundaryOut union.
    // "continue" = architecture-owned generation continuity; the architecture policy defines its
    // target requirements, context window, and frame grid.
    public const string BoundaryOutCut = "cut";
    public const string BoundaryOutContinue = "continue";
    public const string BoundaryOutCrossfade = "crossfade";

}
