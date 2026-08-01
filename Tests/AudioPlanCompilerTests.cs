using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

public class AudioPlanCompilerTests
{
    private static IcLoraClipPlanCompilation CompileIcLoras(ClipSpec clip) =>
        IcLoraPlanCompiler.CompileClip(clip, new(0, 0, 0));

    private static Ltx2AudioPlan CompileLtxAudio(ClipSpec clip)
    {
        IcLoraClipPlanCompilation icLoras = CompileIcLoras(clip);
        return Ltx2AudioPlanCompiler.Compile(
            clip,
            icLoras.PrimaryControlNetSourceIndex);
    }

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
        ClipStageIndex: index,
        ClipStageRawIndex: index);

    private static UploadedMediaSpec Upload(string data = "data:audio/wav;base64,QUJD") =>
        new(data, "clip.wav");

    private static AudioBaseSourcePlan Base(bool hasConfiguredTrack) => new(
        AudioSourceKind.Upload,
        Constants.AudioSourceUpload,
        AceStepFunTrack: null,
        hasConfiguredTrack,
        UploadedMedia: null);

    private static ClipSpec Clip(
        string source = Constants.AudioSourceNative,
        bool audioLength = false,
        bool controlNetLength = false,
        bool reuse = false,
        IReadOnlyList<StageSpec> stages = null,
        UploadedMediaSpec uploadedAudio = null,
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
            Stages: stages ?? [Stage(0)]);

    [Theory]
    [InlineData(Constants.AudioSourceNative, (int)AudioSourceKind.Native)]
    [InlineData(Constants.AudioSourceUpload, (int)AudioSourceKind.Upload)]
    [InlineData(Constants.AudioSourceControlNet, (int)AudioSourceKind.ControlNet)]
    [InlineData("audio7", (int)AudioSourceKind.AceStepFun)]
    public void Compile_maps_lockable_base_source_kinds(string source, int expectedValue)
    {
        AudioSourceKind expected = (AudioSourceKind)expectedValue;
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(
            source: source,
            uploadedAudio: source == Constants.AudioSourceUpload ? Upload() : null));

        Assert.Equal(expected, plan.Base.Kind);
        Assert.Equal(source, plan.Base.RawSource);
        Assert.True(plan.Base.HasConfiguredTrack);
        if (expected == AudioSourceKind.Upload)
        {
            Assert.Equal("data:audio/wav;base64,QUJD", plan.Base.UploadedMedia.Data);
            Assert.Equal("clip.wav", plan.Base.UploadedMedia.FileName);
        }
    }

    [Theory]
    [InlineData((int)AudioSourceKind.Upload, true)]
    [InlineData((int)AudioSourceKind.ControlNet, true)]
    [InlineData((int)AudioSourceKind.AceStepFun, true)]
    [InlineData((int)AudioSourceKind.Native, false)]
    [InlineData((int)AudioSourceKind.Disabled, false)]
    [InlineData((int)AudioSourceKind.Unknown, false)]
    [InlineData((int)AudioSourceKind.External, false)]
    public void Audio_duration_source_policy_matches_the_external_source_contract(
        int sourceValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            AudioSourceKindPolicy.CanDriveClipDuration(
                (AudioSourceKind)sourceValue));
    }

    [Fact]
    public void Compile_makes_controlnet_length_precedence_explicit()
    {
        IcLoraSpec controlNetDrive = new(
            Lora: "drive.safetensors",
            DriveSource: Constants.ControlNetSourceTwo,
            Strength: 1,
            AttentionStrength: 1,
            ControlType: Constants.IcLoraControlNone,
            DriveMedia: null,
            DriveData: IcLoraDriveData.Visual);
        ClipSpec clip = Clip(
            source: Constants.AudioSourceUpload,
            audioLength: true,
            controlNetLength: true,
            uploadedAudio: Upload(),
            icLoras: [controlNetDrive]);
        AudioPlan plan = AudioPlanCompiler.Compile(clip);

        Assert.Equal(AudioLengthOwner.ControlNet, plan.Length.Owner);
        Assert.Equal(1, CompileIcLoras(clip).PrimaryControlNetSourceIndex);
        Assert.Contains(plan.Diagnostics, d => d.Code == "audio.length.controlnet_overrides_audio");
    }

    [Fact]
    public void Compile_uses_timeline_when_no_length_override_is_requested()
    {
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(source: Constants.AudioSourceUpload, uploadedAudio: Upload()));

        Assert.Equal(AudioLengthOwner.Timeline, plan.Length.Owner);
    }

    [Fact]
    public void Compile_reports_controlnet_length_owner_without_a_typed_source()
    {
        ClipSpec clip = Clip(controlNetLength: true);
        AudioPlan plan = AudioPlanCompiler.Compile(clip);
        Ltx2AudioPlan ltx = CompileLtxAudio(clip);

        Assert.Equal(AudioLengthOwner.ControlNet, plan.Length.Owner);
        Assert.Null(CompileIcLoras(clip).PrimaryControlNetSourceIndex);
        Assert.DoesNotContain(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.length.controlnet_owner_has_no_source");
        Assert.Contains(ltx.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.length.controlnet_owner_has_no_source"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Compile_keeps_existing_route_dependent_native_length_matching_explicit()
    {
        ClipSpec nativeClip = Clip();
        ClipSpec uploadClip = Clip(
            source: Constants.AudioSourceUpload,
            uploadedAudio: Upload());

        Assert.Equal(
            AudioLengthOwner.Timeline,
            AudioPlanCompiler.Compile(nativeClip).Length.Owner);
        // Native injection length matching belongs to the LTX plan, not the shared audio plan.
        Ltx2AudioInjectionPlan native = CompileLtxAudio(nativeClip).Injection;
        Ltx2AudioInjectionPlan upload = CompileLtxAudio(uploadClip).Injection;
        Assert.True(native.NonHandoffMatchesAudioLength);
        Assert.False(native.RootHandoffMatchesAudioLength);
        Assert.False(upload.NonHandoffMatchesAudioLength);
        Assert.False(upload.RootHandoffMatchesAudioLength);
    }

    [Fact]
    public void Compile_leaves_segments_to_the_timeline_projection()
    {
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(
            source: Constants.AudioSourceUpload,
            uploadedAudio: Upload()));

        Assert.Empty(plan.Segments.Items);
    }

    [Fact]
    public void Compile_segments_mix_over_a_configured_base_track()
    {
        AudioSegmentPlan plan = AudioSegmentPlanCompiler.Compile(
            [
                new(AudioSourceKind.Upload, null, 4, 0, 1,
                    new("data:audio/wav;base64,QUJD", "clip.wav"), 0.4),
                new(AudioSourceKind.AceStepFun, 3, 1, 0.5, 2, null, 0.7),
            ],
            Base(hasConfiguredTrack: true)).Plan;

        Assert.Equal([1, 4], plan.Items.Select(item => item.StartSeconds));
        Assert.Equal(AudioSourceKind.AceStepFun, plan.Items[0].SourceKind);
        Assert.Equal(3, plan.Items[0].AceStepFunTrack);
        Assert.Equal(0.7, plan.Items[0].Volume);
        Assert.Equal(AudioSourceKind.Upload, plan.Items[1].SourceKind);
        Assert.Equal(0.4, plan.Items[1].Volume);
        Assert.Equal("data:audio/wav;base64,QUJD", plan.Items[1].UploadedMedia.Data);
        Assert.Equal("clip.wav", plan.Items[1].UploadedMedia.FileName);
    }

    [Fact]
    public void Compile_segments_without_a_base_use_preserve_windows()
    {
        AudioPlanComponentResult<AudioSegmentPlan> result = AudioSegmentPlanCompiler.Compile(
            [new(AudioSourceKind.Upload, null, 1, 0, 2,
                new("data:audio/wav;base64,QUJD", "clip.wav"))],
            Base(hasConfiguredTrack: false));

        Assert.Single(result.Plan.Items);
        Assert.Contains(result.Diagnostics, d => d.Code == "audio.segments.preserve_windowed_no_base");
    }

    [Fact]
    public void Compile_audio_reuse_requires_generate_capture_and_reuse_stages()
    {
        Ltx2AudioPlan ineligible = CompileLtxAudio(
            Clip(reuse: true, stages: [Stage(0), Stage(1)]));
        Ltx2AudioPlan eligible = CompileLtxAudio(
            Clip(reuse: true, stages: [Stage(0), Stage(1), Stage(2)]));

        Assert.True(ineligible.Reuse.IsRequested);
        Assert.False(ineligible.Reuse.IsEligible);
        Assert.Empty(ineligible.Diagnostics);
        Assert.True(eligible.Reuse.IsEligible);
        Assert.Equal(1, eligible.Reuse.CaptureStageIndex);
        Assert.Equal(2, eligible.Reuse.ReuseFromStageIndex);
    }

    [Fact]
    public void Compile_unknown_source_warns_and_falls_back_to_disabled()
    {
        AudioPlan plan = AudioPlanCompiler.Compile(Clip(source: "not-an-audio-source"));

        Assert.Equal(AudioSourceKind.Disabled, plan.Base.Kind);
        PlanDiagnostic unknown = Assert.Single(
            plan.Diagnostics.Where(d => d.Code == AudioBaseSourcePlanCompiler.UnknownSourceCode));
        Assert.Equal(PlanDiagnosticSeverity.Warning, unknown.Severity);
    }
}
