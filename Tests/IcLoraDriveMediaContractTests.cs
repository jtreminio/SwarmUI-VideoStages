using VideoStages.Architectures.Ltx2.Planning;
using Xunit;

namespace VideoStages.Tests;

public class IcLoraDriveMediaContractTests
{
    [Theory]
    [InlineData("data:audio/wav;base64,QUJD", (int)IcLoraDriveMediaKind.Audio)]
    [InlineData("data:video/mp4;base64,QUJD", (int)IcLoraDriveMediaKind.Video)]
    public void LipDub_consumes_only_the_audio_stream(string data, int expectedKind)
    {
        ClipSpec clip = Clip([
            LipDub(new UploadedMediaSpec(data, "speaker.media")),
        ]);

        IcLoraPlan plan = Assert.Single(IcLoraPlanCompiler.Compile(clip, clip.Stages[0]));

        Assert.Equal(
            IcLoraDriveMediaConsumption.AudioReference,
            plan.MediaContract.Consumption);
        Assert.Equal((IcLoraDriveMediaKind)expectedKind, plan.DriveMedia.Kind);
        Assert.Equal(IcLoraVisualGuideSourceKind.LoaderOnly, plan.VisualGuide.Kind);
        Assert.False(plan.VisualGuide.HasGuide);
        Assert.Empty(IcLoraPlanCompiler.ValidateClip(clip));
    }

    [Theory]
    [InlineData(null, "ltx2.ic-lora.audio-drive-media-missing")]
    [InlineData("data:image/png;base64,QUJD", "ltx2.ic-lora.audio-drive-media-unsupported")]
    public void LipDub_rejects_missing_or_image_drive_media(string data, string code)
    {
        ClipSpec clip = Clip([
            LipDub(data is null ? null : new UploadedMediaSpec(data, "speaker.png")),
        ]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void Visual_ic_lora_rejects_audio_drive_media()
    {
        ClipSpec clip = Clip([
            new(
                "visual.safetensors",
                Constants.IcLoraSourceUpload,
                1,
                1,
                Constants.IcLoraControlNone,
                new UploadedMediaSpec("data:audio/wav;base64,QUJD", "speaker.wav"),
                Preset: "custom"),
        ]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.upload-kind-unsupported");
    }

    [Fact]
    public void Two_LipDubs_may_target_disjoint_stages_but_not_the_same_stage()
    {
        ClipSpec disjoint = Clip(
            [
                LipDub(Audio("one.wav"), stage: 0),
                LipDub(Audio("two.wav"), stage: 1),
            ],
            [Stage(0), Stage(1)]);
        ClipSpec overlapping = Clip(
            [
                LipDub(Audio("one.wav")),
                LipDub(Audio("two.wav"), stage: 1),
            ],
            [Stage(0), Stage(1)]);

        Assert.DoesNotContain(
            IcLoraPlanCompiler.ValidateClip(disjoint),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.audio-drive-overlap");
        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(overlapping),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.audio-drive-overlap");
    }

    [Fact]
    public void LipDub_cannot_target_a_passthrough_stage()
    {
        ClipSpec clip = Clip(
            [LipDub(Audio("speaker.wav"))],
            [Stage(0) with { Control = 0 }]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.audio-drive-passthrough");
    }

    private static UploadedMediaSpec Audio(string fileName) =>
        new("data:audio/wav;base64,QUJD", fileName);

    private static IcLoraSpec LipDub(UploadedMediaSpec media, int stage = -1) => new(
        IcLoraWeights.AutoModelToken,
        Constants.IcLoraSourceUpload,
        1,
        1,
        Constants.IcLoraControlNone,
        media,
        Preset: IcLoraDriveMediaContracts.LipDubPreset,
        Stage: stage);

    private static ClipSpec Clip(
        IReadOnlyList<IcLoraSpec> icLoras,
        IReadOnlyList<StageSpec> stages = null) => new(
        Id: 0,
        Frames: 121,
        AudioSource: Constants.AudioSourceNative,
        IcLoras: icLoras,
        SaveAudioTrack: false,
        ClipLengthFromAudio: false,
        ClipLengthFromControlNet: false,
        ReuseAudio: false,
        UploadedAudio: null,
        ImageRefs: [],
        Stages: stages ?? [Stage(0)]);

    private static StageSpec Stage(int index) => new(
        Id: index,
        Control: 1,
        Upscale: 1,
        UpscaleMethod: "pixel-lanczos",
        Model: "ltx-2.3",
        Steps: 8,
        CfgScale: 1,
        Sampler: "euler",
        Scheduler: "normal",
        ImageReference: "Generated",
        ClipStageIndex: index,
        ClipStageRawIndex: index);
}
