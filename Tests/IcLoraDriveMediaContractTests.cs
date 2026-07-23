using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

public class IcLoraDriveMediaContractTests
{
    [Theory]
    [InlineData("data:audio/wav;base64,QUJD", (int)IcLoraDriveMediaKind.Audio)]
    [InlineData("data:video/mp4;base64,QUJD", (int)IcLoraDriveMediaKind.Video)]
    public void Explicit_audio_intent_consumes_only_the_audio_stream(
        string data,
        int expectedKind)
    {
        ClipSpec clip = Clip([
            AudioDrive(new UploadedMediaSpec(data, "speaker.media"), preset: "custom-audio"),
        ]);

        IcLoraPlan plan = Assert.Single(IcLoraPlanCompiler.Compile(clip, clip.Stages[0]));

        Assert.Equal(IcLoraDriveData.Audio, plan.MediaContract.DriveData);
        Assert.Equal((IcLoraDriveMediaKind)expectedKind, plan.DriveMedia.Kind);
        Assert.Equal(IcLoraMediaSourceKind.Upload, plan.MediaInput.Source);
        Assert.False(plan.HasVisualGuide);
        Assert.True(plan.HasAudioReference);
        Assert.Empty(IcLoraPlanCompiler.ValidateClip(clip));
    }

    [Theory]
    [InlineData(null, "ltx2.ic-lora.drive-media-missing")]
    [InlineData("data:image/png;base64,QUJD", "ltx2.ic-lora.drive-media-kind-unsupported")]
    public void Explicit_audio_intent_rejects_missing_or_image_drive_media(
        string data,
        string code)
    {
        ClipSpec clip = Clip([
            AudioDrive(data is null ? null : new UploadedMediaSpec(data, "speaker.png")),
        ]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void Preset_name_does_not_change_drive_media_interpretation()
    {
        ClipSpec clip = Clip([
            AudioDrive(Audio("one.wav"), preset: "lipdub", stage: 0),
            AudioDrive(Audio("two.wav"), preset: "totally-custom", stage: 1),
        ], [Stage(0), Stage(1)]);

        IcLoraPlan lipDub = Assert.Single(IcLoraPlanCompiler.Compile(clip, clip.Stages[0]));
        IcLoraPlan custom = Assert.Single(IcLoraPlanCompiler.Compile(clip, clip.Stages[1]));

        Assert.Equal(lipDub.MediaContract, custom.MediaContract);
        Assert.Equal(IcLoraDriveData.Audio, custom.MediaContract.DriveData);
        Assert.True(custom.HasAudioReference);
        Assert.False(custom.HasVisualGuide);
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
                DriveData: IcLoraDriveData.Visual,
                Preset: "custom"),
        ]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.drive-media-kind-unsupported");
    }

    [Theory]
    [InlineData("data:image/png;base64,QUJD", false)]
    [InlineData("data:video/mp4;base64,QUJD", true)]
    public void Image_only_contract_accepts_image_upload_and_rejects_video_upload(
        string data,
        bool shouldReject)
    {
        ClipSpec clip = Clip([
            new(
                "image-only.safetensors",
                Constants.IcLoraSourceUpload,
                1,
                1,
                Constants.IcLoraControlNone,
                new UploadedMediaSpec(data, "drive.media"),
                DriveData: IcLoraDriveData.Visual,
                DriveMediaKinds: ["image"]),
        ]);

        IReadOnlyList<VideoPlanDiagnostic> diagnostics =
            IcLoraPlanCompiler.ValidateClip(clip);

        Assert.Equal(
            shouldReject,
            diagnostics.Any(diagnostic =>
                diagnostic.Code == "ltx2.ic-lora.drive-media-kind-unsupported"));
    }

    [Theory]
    [InlineData((int)ArchitectureEntryMode.ImageToVideo, false)]
    [InlineData((int)ArchitectureEntryMode.SourceVideo, true)]
    public void Image_only_contract_accepts_image_incoming_and_rejects_video_incoming(
        int entryMode,
        bool shouldReject)
    {
        ClipSpec clip = Clip([
            new(
                "image-only.safetensors",
                Constants.IcLoraSourceIncoming,
                1,
                1,
                Constants.IcLoraControlNone,
                null,
                DriveData: IcLoraDriveData.Visual,
                DriveMediaKinds: ["image"]),
        ]);
        ArchitectureClipCompileContext context = new(
            640,
            360,
            24,
            (ArchitectureEntryMode)entryMode);

        IReadOnlyList<VideoPlanDiagnostic> diagnostics =
            IcLoraPlanCompiler.ValidateClip(clip, context);

        Assert.Equal(
            shouldReject,
            diagnostics.Any(diagnostic =>
                diagnostic.Code == "ltx2.ic-lora.drive-media-kind-unsupported"));
    }

    [Theory]
    [InlineData((int)IcLoraDriveData.Visual, "audio")]
    [InlineData((int)IcLoraDriveData.Audio, "image")]
    [InlineData((int)IcLoraDriveData.None, "video")]
    public void Contradictory_explicit_media_kinds_are_diagnostics(
        int driveData,
        string mediaKind)
    {
        ClipSpec clip = Clip([
            new(
                "adapter.safetensors",
                Constants.IcLoraSourceUpload,
                1,
                1,
                Constants.IcLoraControlNone,
                null,
                DriveData: (IcLoraDriveData)driveData,
                DriveMediaKinds: [mediaKind]),
        ]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code
                == "ltx2.ic-lora.drive-media-kinds-contradictory");
    }

    [Theory]
    [InlineData("future")]
    [InlineData("")]
    public void Malformed_explicit_media_kinds_are_diagnostics(string mediaKind)
    {
        ClipSpec clip = Clip([
            new(
                "adapter.safetensors",
                Constants.IcLoraSourceUpload,
                1,
                1,
                Constants.IcLoraControlNone,
                null,
                DriveData: IcLoraDriveData.Visual,
                DriveMediaKinds: [mediaKind]),
        ]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code
                == "ltx2.ic-lora.drive-media-kinds-malformed");
    }

    [Fact]
    public void Upload_without_media_never_falls_back_to_the_clip_input()
    {
        ClipSpec clip = Clip([
            new(
                "visual.safetensors",
                Constants.IcLoraSourceUpload,
                1,
                1,
                Constants.IcLoraControlNone,
                null,
                DriveData: IcLoraDriveData.Visual),
        ]) with
        {
            SourceVideo = new(
                "data:video/mp4;base64,QUJD",
                "source.mp4",
                0),
        };

        IcLoraPlan plan = Assert.Single(IcLoraPlanCompiler.Compile(clip, clip.Stages[0]));

        Assert.Equal(IcLoraMediaSourceKind.Upload, plan.MediaInput.Source);
        Assert.False(plan.MediaInput.HasInput);
        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.drive-media-missing");
    }

    [Theory]
    [InlineData("Incoming")]
    [InlineData("ControlNet 1")]
    public void Non_upload_sources_reject_configured_drive_media(string driveSource)
    {
        ClipSpec clip = Clip([
            new(
                "visual.safetensors",
                driveSource,
                1,
                1,
                Constants.IcLoraControlNone,
                new UploadedMediaSpec("data:video/mp4;base64,QUJD", "ignored.mp4"),
                DriveData: IcLoraDriveData.Visual),
        ]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.drive-media-source-mismatch");
    }

    [Theory]
    [InlineData("Incoming")]
    [InlineData("ControlNet 1")]
    [InlineData("future-source")]
    public void Model_only_contract_rejects_hidden_non_upload_source(string driveSource)
    {
        ClipSpec clip = Clip([
            new(
                "model-only.safetensors",
                driveSource,
                1,
                1,
                Constants.IcLoraControlNone,
                null,
                DriveData: IcLoraDriveData.None,
                DriveMediaKinds: []),
        ]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.drive-source-contradictory");
    }

    [Fact]
    public void Malformed_drive_data_is_distinct_from_missing_none_intent()
    {
        ClipSpec clip = Clip([
            new(
                "adapter.safetensors",
                Constants.IcLoraSourceUpload,
                1,
                1,
                Constants.IcLoraControlNone,
                null,
                DriveData: (IcLoraDriveData)(-1)),
        ]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.drive-data-unsupported");
    }

    [Fact]
    public void Planning_records_the_declarative_dimension_downscale_factor()
    {
        ClipSpec clip = Clip([
            new(
                IcLoraWeights.AutoModelToken,
                Constants.IcLoraSourceUpload,
                1,
                1,
                Constants.IcLoraControlNone,
                null,
                DriveData: IcLoraDriveData.None,
                Preset: "pixel-spatial-upscaler-x4"),
        ]);

        IcLoraPlan plan = Assert.Single(IcLoraPlanCompiler.Compile(clip, clip.Stages[0]));

        Assert.Equal(4, plan.DimensionDownscaleFactor);
    }

    [Fact]
    public void Incoming_visual_uses_the_image_entry_for_image_to_video()
    {
        ClipSpec clip = Clip([
            Incoming(IcLoraDriveData.Visual),
        ]);
        ArchitectureClipCompileContext context = new(
            640,
            360,
            24,
            ArchitectureEntryMode.ImageToVideo);

        IcLoraPlan plan = Assert.Single(
            IcLoraPlanCompiler.Compile(clip, clip.Stages[0], context));

        Assert.Equal(IcLoraMediaSourceKind.Incoming, plan.MediaInput.Source);
        Assert.Equal(IcLoraDriveMediaKind.Image, plan.MediaInput.Kind);
        Assert.Empty(IcLoraPlanCompiler.ValidateClip(clip, context));
    }

    [Fact]
    public void Incoming_requires_contextual_media_at_a_text_to_video_entry()
    {
        ClipSpec clip = Clip([
            Incoming(IcLoraDriveData.Visual),
        ]);
        ArchitectureClipCompileContext context = new(
            640,
            360,
            24,
            ArchitectureEntryMode.TextToVideo);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip, context),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.incoming-unavailable");
    }

    [Fact]
    public void Incoming_can_consume_the_previous_clip_output_even_after_a_cut()
    {
        ClipSpec clip = Clip([
            Incoming(IcLoraDriveData.Audio, preset: "custom-audio"),
        ]);
        ArchitectureClipCompileContext context = new(
            640,
            360,
            24,
            ArchitectureEntryMode.TextToVideo,
            HasPreviousClipOutput: true);

        IcLoraPlan plan = Assert.Single(
            IcLoraPlanCompiler.Compile(clip, clip.Stages[0], context));

        Assert.Equal(IcLoraDriveMediaKind.Video, plan.MediaInput.Kind);
        Assert.True(plan.HasAudioReference);
        Assert.Empty(IcLoraPlanCompiler.ValidateClip(clip, context));
    }

    [Fact]
    public void Two_audio_drives_may_target_disjoint_stages_but_not_the_same_stage()
    {
        ClipSpec disjoint = Clip(
            [
                AudioDrive(Audio("one.wav"), stage: 0),
                AudioDrive(Audio("two.wav"), stage: 1),
            ],
            [Stage(0), Stage(1)]);
        ClipSpec overlapping = Clip(
            [
                AudioDrive(Audio("one.wav")),
                AudioDrive(Audio("two.wav"), stage: 1),
            ],
            [Stage(0), Stage(1)]);

        Assert.DoesNotContain(
            IcLoraPlanCompiler.ValidateClip(disjoint),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.audio-drive-overlap");
        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(overlapping),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.audio-drive-overlap");
    }

    [Theory]
    [InlineData((int)IcLoraDriveData.None)]
    [InlineData((int)IcLoraDriveData.Visual)]
    [InlineData((int)IcLoraDriveData.Audio)]
    public void Every_ic_lora_contract_rejects_a_passthrough_stage(int driveData)
    {
        IcLoraDriveData data = (IcLoraDriveData)driveData;
        UploadedMediaSpec media = data switch
        {
            IcLoraDriveData.Visual =>
                new("data:image/png;base64,QUJD", "guide.png"),
            IcLoraDriveData.Audio => Audio("speaker.wav"),
            _ => null,
        };
        ClipSpec clip = Clip(
            [
                new(
                    "adapter.safetensors",
                    Constants.IcLoraSourceUpload,
                    1,
                    1,
                    Constants.IcLoraControlNone,
                    media,
                    DriveData: data),
            ],
            [Stage(0) with { Control = 0 }]);

        Assert.Contains(
            IcLoraPlanCompiler.ValidateClip(clip),
            diagnostic => diagnostic.Code == "ltx2.ic-lora.passthrough-stage");
    }

    private static UploadedMediaSpec Audio(string fileName) =>
        new("data:audio/wav;base64,QUJD", fileName);

    private static IcLoraSpec AudioDrive(
        UploadedMediaSpec media,
        string preset = "lipdub",
        int stage = -1) => new(
        "audio-adapter.safetensors",
        Constants.IcLoraSourceUpload,
        1,
        1,
        Constants.IcLoraControlNone,
        media,
        DriveData: IcLoraDriveData.Audio,
        Preset: preset,
        Stage: stage);

    private static IcLoraSpec Incoming(
        IcLoraDriveData data,
        string preset = "custom") => new(
        "adapter.safetensors",
        Constants.IcLoraSourceIncoming,
        1,
        1,
        Constants.IcLoraControlNone,
        null,
        DriveData: data,
        Preset: preset);

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
