using VideoStages.Authoring;

namespace VideoStages.Tests;

/// <summary>
/// The shared owner of the authored spec builders. Every field is a named parameter, so adding a
/// record component here does not touch a dozen positional call sites.
/// <para>
/// Test files keep their own thin wrappers where their defaults genuinely differ (steps, CFG, the
/// model name a family expects); those wrappers call through here rather than the constructor.
/// </para>
/// </summary>
internal static class SpecFixtures
{
    internal static StageSpec Stage(
        int id,
        string model = "ltx-2",
        double control = 1,
        double upscale = 1,
        string upscaleMethod = "pixel-lanczos",
        int steps = 12,
        double cfgScale = 4.5,
        string sampler = "euler",
        string scheduler = "normal",
        string imageReference = "Generated",
        int? clipStageIndex = null,
        int? clipStageRawIndex = null,
        IReadOnlyList<double> icLoraStrengths = null,
        IReadOnlyList<double> frameRefStrengths = null,
        IReadOnlyList<LoraRef> loras = null,
        IReadOnlyList<double> loraWeights = null,
        RetakeWindowSpec retakeWindow = null) =>
        new(
            id,
            control,
            upscale,
            upscaleMethod,
            model,
            steps,
            cfgScale,
            sampler,
            scheduler,
            imageReference,
            ClipStageIndex: clipStageIndex ?? 0,
            ClipStageRawIndex: clipStageRawIndex ?? clipStageIndex ?? 0,
            IcLoraStrengths: icLoraStrengths,
            FrameRefStrengths: frameRefStrengths,
            Loras: loras,
            LoraWeights: loraWeights,
            RetakeWindow: retakeWindow);

    internal static ClipSpec Clip(
        int id,
        IReadOnlyList<StageSpec> stages,
        int? frames = 49,
        string audioSource = null,
        InitVideoSpec initVideo = null,
        IReadOnlyList<FrameRefSpec> frameRefs = null,
        IReadOnlyList<IcLoraSpec> icLoras = null,
        string boundaryOut = Constants.BoundaryOutCut,
        int boundaryOutOverlap = 0) =>
        new(
            id,
            frames,
            audioSource ?? MediaSource.Native,
            icLoras ?? [],
            SaveAudioTrack: false,
            ClipLengthFromAudio: false,
            ClipLengthFromControlNet: false,
            ReuseAudio: false,
            UploadedAudio: null,
            FrameRefs: frameRefs ?? [],
            Stages: stages,
            BoundaryOut: boundaryOut,
            BoundaryOutOverlap: boundaryOutOverlap,
            InitVideo: initVideo);

    /// <summary>A clip that refines uploaded footage instead of generating from noise.</summary>
    internal static ClipSpec SourcedClip(int id, IReadOnlyList<StageSpec> stages, int? frames = 49) =>
        Clip(id, stages, frames, initVideo: new InitVideoSpec("data", "source.mp4", StartSeconds: 0));
}
