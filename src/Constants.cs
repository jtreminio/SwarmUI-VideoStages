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
    public const string AudioSourceControlNet = "ControlNet";
    // The uploaded audio is a speaker-identity sample (LTXVSetAudioRefTokens), not a locked track:
    // the model generates new speech matching the prompt in that voice. LTX-2 only.
    public const string AudioSourceVoiceRef = "Voice Reference";
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
    // "[AUTO]" as an entry's Lora means "the selected preset's weights": the frontend triggers
    // VideoStagesDownloadIcLoraWS, which saves them to <lora dir>/LTX-2/IC-LoRA/<original upstream
    // filename>, and the backend resolves the same path via IcLoraWeights. Mirrors IC_LORA_AUTO /
    // IC_LORA_AUTO_FOLDER in frontend/constants.ts.
    public const string IcLoraAutoModel = "[AUTO]";
    public const string IcLoraAutoModelFolder = "LTX-2/IC-LoRA";
    // Geometry-estimation models (ComfyUI/models/geometry_estimation/). Depth renders through core
    // Depth Anything 3 (mono model, any variant); normal maps need MoGe — the file the official
    // ComfyUI LTX-2.3 IC-LoRA workflow ships with.
    internal const string Da3ModelFileName = "depth_anything_3_mono_large.safetensors";
    internal const string MoGeModelFileName = "moge_2_vitl_normal_fp16.safetensors";

    // Outgoing boundary between clip N and N+1; mirrors the frontend BoundaryOut union.
    // "continue" = generation-time continuity: the next clip is generated from this clip's final frame
    // (first-frame guide at strength 1) and the merge collapses the duplicated seam frame via a 1-frame
    // overlap. Degrades to "cut" when the next clip can't consume it (non-LTX-2 first stage, explicit
    // first-frame ref, or unknown frame count).
    public const string BoundaryOutCut = "cut";
    public const string BoundaryOutContinue = "continue";
    public const string BoundaryOutCrossfade = "crossfade";
}
