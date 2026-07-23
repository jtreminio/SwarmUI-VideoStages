using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

public class AudioPlanCompilerTests
{
    private static StageSpec Stage(int index) => new(
        Id: index,
        Control: 1.0,
        Upscale: 1.0,
        UpscaleMethod: "pixel-lanczos",
        Model: "ltx-2",
        Steps: 8,
        CfgScale: 1.0,
        Sampler: "euler",
        Scheduler: "normal",
        ImageReference: "Generated",
        ClipStageIndex: index);

    private static UploadedAudioSpec Upload(string data = "data:audio/wav;base64,QUJD") =>
        new(data, "clip.wav");

    private static ClipSpec Clip(
        string source = Constants.AudioSourceNative,
        bool audioLength = false,
        bool controlNetLength = false,
        bool reuse = false,
        IReadOnlyList<StageSpec> stages = null,
        UploadedAudioSpec uploadedAudio = null,
        IReadOnlyList<AudioSegmentSpec> segments = null,
        IReadOnlyList<IcLoraSpec> icLoras = null) => new(
            Id: 0,
            Frames: 241,
            AudioSource: source,
            IcLoras: icLoras,
            SaveAudioTrack: false,
            ClipLengthFromAudio: audioLength,
            ClipLengthFromControlNet: controlNetLength,
            ReuseAudio: reuse,
            UploadedAudio: uploadedAudio,
            ImageRefs: [],
            Stages: stages ?? [Stage(0)],
            AudioSegments: segments);

    [Theory]
    [InlineData(Constants.AudioSourceNative, (int)AudioBaseSourceKind.Native)]
    [InlineData(Constants.AudioSourceUpload, (int)AudioBaseSourceKind.Upload)]
    [InlineData(Constants.AudioSourceControlNet, (int)AudioBaseSourceKind.ControlNet)]
    [InlineData("audio7", (int)AudioBaseSourceKind.AceStepFun)]
    public void Compile_maps_lockable_base_source_kinds(string source, int expectedValue)
    {
        AudioBaseSourceKind expected = (AudioBaseSourceKind)expectedValue;
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(
            source: source,
            uploadedAudio: source == Constants.AudioSourceUpload ? Upload() : null));

        Assert.Equal(expected, plan.Base.Kind);
        Assert.Equal(source, plan.Base.RawSource);
        Assert.Equal(expected != AudioBaseSourceKind.None, plan.Base.HasConfiguredTrack);
        if (expected == AudioBaseSourceKind.Upload)
        {
            Assert.Equal("data:audio/wav;base64,QUJD", plan.Base.UploadedMedia.Data);
            Assert.Equal("clip.wav", plan.Base.UploadedMedia.FileName);
        }
    }

    [Fact]
    public void Compile_models_voice_reference_separately_from_the_base_track()
    {
        IcLoraSpec driveVoice = new(
            Lora: "voice.safetensors",
            Source: Constants.IcLoraSourceUpload,
            Strength: 1,
            AttentionStrength: 1,
            ControlType: Constants.IcLoraControlNone,
            Video: Upload(),
            DriveAudioRef: true);

        AudioPlan plan = AudioPlanCompiler.Compile(Clip(
            source: Constants.AudioSourceNative,
            icLoras: [driveVoice]));

        Assert.Equal(AudioBaseSourceKind.Native, plan.Base.Kind);
        Assert.Equal(AudioVoiceReferenceKind.IcLoraDriveVideo, plan.VoiceReference.Kind);
        Assert.True(plan.VoiceReference.IsRequested);
        Assert.True(plan.VoiceReference.HasConfiguredSample);
        Assert.Equal(0, plan.VoiceReference.IcLoraEntryIndex);
        Assert.Equal("data:audio/wav;base64,QUJD", plan.VoiceReference.Media.Data);
        Assert.Equal("clip.wav", plan.VoiceReference.Media.FileName);
    }

    [Fact]
    public void Compile_drive_audio_reference_without_media_has_stable_diagnostic()
    {
        IcLoraSpec missingDrive = new(
            Lora: "voice.safetensors",
            Source: Constants.IcLoraSourceUpload,
            Strength: 1,
            AttentionStrength: 1,
            ControlType: Constants.IcLoraControlNone,
            Video: null,
            DriveAudioRef: true);

        AudioPlan plan = AudioPlanCompiler.Compile(Clip(icLoras: [missingDrive]));

        Assert.Equal(AudioVoiceReferenceKind.IcLoraDriveVideo, plan.VoiceReference.Kind);
        Assert.True(plan.VoiceReference.IsRequested);
        Assert.False(plan.VoiceReference.HasConfiguredSample);
        Assert.Equal(0, plan.VoiceReference.IcLoraEntryIndex);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.voice_reference.drive_media_missing");
    }

    [Fact]
    public void Compile_voice_reference_source_has_no_locked_track_and_reports_missing_upload()
    {
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(source: Constants.AudioSourceVoiceRef));

        Assert.Equal(AudioBaseSourceKind.None, plan.Base.Kind);
        Assert.False(plan.Base.HasConfiguredTrack);
        Assert.Equal(AudioVoiceReferenceKind.ClipUpload, plan.VoiceReference.Kind);
        Assert.False(plan.VoiceReference.HasConfiguredSample);
        Assert.Contains(plan.Diagnostics, d => d.Code == "audio.voice_reference.missing_sample");
    }

    [Fact]
    public void Compile_makes_controlnet_length_precedence_explicit()
    {
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(
            source: Constants.AudioSourceUpload,
            audioLength: true,
            controlNetLength: true,
            uploadedAudio: Upload()));

        Assert.Equal(AudioLengthOwner.ControlNet, plan.Length.Owner);
        Assert.True(plan.Length.AudioWasRequested);
        Assert.True(plan.Length.ControlNetWasRequested);
        Assert.Contains(plan.Diagnostics, d => d.Code == "audio.length.controlnet_overrides_audio");
    }

    [Fact]
    public void Compile_uses_timeline_when_no_length_override_is_requested()
    {
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(source: Constants.AudioSourceUpload, uploadedAudio: Upload()));

        Assert.Equal(AudioLengthOwner.Timeline, plan.Length.Owner);
    }

    [Fact]
    public void Compile_keeps_existing_route_dependent_native_length_matching_explicit()
    {
        AudioPlan native = AudioPlanCompiler.Compile(Clip());
        AudioPlan upload = AudioPlanCompiler.Compile(Clip(
            source: Constants.AudioSourceUpload,
            uploadedAudio: Upload()));

        Assert.Equal(AudioLengthOwner.Timeline, native.Length.Owner);
        Assert.True(native.Length.NonHandoffInjectionMatchesAudioLength);
        Assert.False(native.Length.RootHandoffInjectionMatchesAudioLength);
        Assert.False(upload.Length.NonHandoffInjectionMatchesAudioLength);
        Assert.False(upload.Length.RootHandoffInjectionMatchesAudioLength);
    }

    [Fact]
    public void Compile_segments_mix_over_a_configured_base_track()
    {
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(
            source: Constants.AudioSourceUpload,
            uploadedAudio: Upload(),
            segments:
            [
                new(Upload(), StartSeconds: 4, TrimStartSeconds: 0, LengthSeconds: 1),
                new(null, StartSeconds: 1, TrimStartSeconds: 0.5, LengthSeconds: 2, AceStepFunSource: "audio3")
            ]));

        Assert.Equal(AudioSegmentMode.MixOverBase, plan.Segments.Mode);
        Assert.Equal(AudioSegmentBaseResolutionRequirement.ResolveAtExecution,
            plan.Segments.BaseResolutionRequirement);
        Assert.Equal([1, 4], plan.Segments.Items.Select(item => item.StartSeconds));
        Assert.Equal(AudioSegmentSourceKind.AceStepFun, plan.Segments.Items[0].SourceKind);
        Assert.Equal(3, plan.Segments.Items[0].AceStepFunTrack);
        Assert.Equal(AudioSegmentSourceKind.Upload, plan.Segments.Items[1].SourceKind);
        Assert.Equal("data:audio/wav;base64,QUJD", plan.Segments.Items[1].UploadedMedia.Data);
        Assert.Equal("clip.wav", plan.Segments.Items[1].UploadedMedia.FileName);
    }

    [Fact]
    public void Compile_segments_without_a_base_use_preserve_windows()
    {
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(
            source: Constants.AudioSourceVoiceRef,
            uploadedAudio: Upload(),
            segments: [new(Upload(), StartSeconds: 1, TrimStartSeconds: 0, LengthSeconds: 2)]));

        Assert.Equal(AudioSegmentMode.PreserveWindowedNoBase, plan.Segments.Mode);
        Assert.Equal(AudioSegmentBaseResolutionRequirement.NoBaseConfigured,
            plan.Segments.BaseResolutionRequirement);
        Assert.Contains(plan.Diagnostics, d => d.Code == "audio.segments.preserve_windowed_no_base");
    }

    [Fact]
    public void Compile_segments_with_runtime_resolved_bases_defers_the_final_mode_to_execution()
    {
        AudioSegmentSpec segment = new(Upload(), StartSeconds: 1, TrimStartSeconds: 0, LengthSeconds: 2);
        AudioPlan native = AudioPlanCompiler.Compile(Clip(segments: [segment]));
        AudioPlan controlNet = AudioPlanCompiler.Compile(Clip(
            source: Constants.AudioSourceControlNet,
            segments: [segment]));
        AudioPlan ace = AudioPlanCompiler.Compile(Clip(source: "audio1", segments: [segment]));

        // A configured source is not proof that its host artifact exists. The executor must use
        // PreserveWindowedNoBase if resolution yields null, exactly as the current workflow path does.
        Assert.All([native, controlNet, ace], plan =>
        {
            Assert.Equal(AudioSegmentMode.MixOverBase, plan.Segments.Mode);
            Assert.Equal(AudioSegmentBaseResolutionRequirement.ResolveAtExecution,
                plan.Segments.BaseResolutionRequirement);
        });
    }

    [Fact]
    public void Compile_audio_reuse_requires_generate_capture_and_reuse_stages()
    {
        AudioPlan ineligible = AudioPlanCompiler.Compile(Clip(reuse: true, stages: [Stage(0), Stage(1)]));
        AudioPlan eligible = AudioPlanCompiler.Compile(Clip(reuse: true, stages: [Stage(0), Stage(1), Stage(2)]));

        Assert.True(ineligible.Reuse.IsRequested);
        Assert.False(ineligible.Reuse.IsEligible);
        Assert.Contains(ineligible.Diagnostics, d => d.Code == "audio.reuse.requires_three_stages");
        Assert.True(eligible.Reuse.IsEligible);
        Assert.Equal(1, eligible.Reuse.CaptureStageIndex);
        Assert.Equal(2, eligible.Reuse.ReuseFromStageIndex);
    }

    [Fact]
    public void Compile_unknown_source_keeps_backend_native_fallback_visible()
    {
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(source: "not-an-audio-source"));

        Assert.Equal(AudioBaseSourceKind.Native, plan.Base.Kind);
        Assert.Contains(plan.Diagnostics, d => d.Code == "audio.source.unknown_defaults_to_native");
    }
}
