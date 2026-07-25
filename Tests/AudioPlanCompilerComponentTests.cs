using System.Text.Json;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

public class AudioPlanCompilerComponentTests
{
    [Theory]
    [MemberData(nameof(BaseSourceClips))]
    public void Facade_matches_focused_component_outputs_for_each_base_source(ClipSpec clip)
    {
        AudioPlanComponentResult<AudioBaseSourcePlan> baseSource = AudioBaseSourcePlanCompiler.Compile(clip);
        AudioPlanComponentResult<AudioLengthPlan> length = AudioLengthPlanCompiler.Compile(clip, baseSource.Plan);

        AudioPlan assembled = new(
            baseSource.Plan,
            length.Plan,
            new([]),
            [.. baseSource.Diagnostics, .. length.Diagnostics]);

        Assert.Equal(Serialize(assembled), Serialize(AudioPlanCompiler.Compile(clip)));
    }

    public static IEnumerable<object[]> BaseSourceClips()
    {
        yield return [Clip()];
        yield return [Clip(source: Constants.AudioSourceUpload, uploadedAudio: Upload())];
        yield return [Clip(source: "audio4")];
        yield return [Clip(source: Constants.AudioSourceControlNet)];
    }

    [Fact]
    public void Segment_component_preserves_sorted_windows()
    {
        ClipSpec clip = Clip(source: Constants.AudioSourceUpload, uploadedAudio: Upload());
        AudioBaseSourcePlan baseSource = AudioBaseSourcePlanCompiler.Compile(clip).Plan;
        AudioPlanComponentResult<AudioSegmentPlan> component = AudioSegmentPlanCompiler.Compile(
            [
                new(AudioSourceKind.Upload, null, 3, 1, 2,
                    new("data:audio/wav;base64,VEhSRUU=", "clip.wav")),
                new(AudioSourceKind.AceStepFun, 2, 1, 0.5, 1, null),
            ],
            baseSource);

        Assert.Empty(AudioPlanCompiler.Compile(clip).Segments.Items);
        Assert.Equal([1, 3], component.Plan.Items.Select(item => item.StartSeconds));
        Assert.Equal(AudioSourceKind.AceStepFun, component.Plan.Items[0].SourceKind);
        Assert.Equal(AudioSourceKind.Upload, component.Plan.Items[1].SourceKind);
    }

    [Fact]
    public void Reuse_component_and_facade_preserve_ineligible_plan()
    {
        ClipSpec clip = Clip(reuse: true, stages: [Stage(0), Stage(1)]);

        AudioPlanComponentResult<AudioReusePlan> component = AudioReusePlanCompiler.Compile(clip);
        Ltx2AudioPlan facade = Ltx2AudioPlanCompiler.Compile(clip);

        Assert.Equal(component.Plan, facade.Reuse);
        Assert.False(component.Plan.IsEligible);
        Assert.Empty(component.Diagnostics);
    }

    [Fact]
    public void Facade_keeps_component_diagnostics_in_stable_order()
    {
        ClipSpec clip = Clip(
            source: "unrecognized",
            audioLength: true,
            controlNetLength: true,
            reuse: true);

        AudioPlan plan = AudioPlanCompiler.Compile(clip);
        Ltx2AudioPlan ltx = Ltx2AudioPlanCompiler.Compile(clip);

        Assert.Equal(
        [
            "audio.source.unknown",
            "audio.length.controlnet_overrides_audio",
        ],
            plan.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Equal(
        [
            "audio.segment.ignored_invalid_window",
            "audio.segment.ignored_no_source",
        ],
            AudioSegmentPlanCompiler.Compile(
                [
                    new(AudioSourceKind.Upload, null, -1, 0, 1, null),
                    new(AudioSourceKind.Upload, null, 0, 0, 1, null),
                ],
                AudioBaseSourcePlanCompiler.Compile(clip).Plan)
                .Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Equal(
        [
            "audio.length.controlnet_owner_has_no_source",
        ],
            ltx.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    private static string Serialize<T>(T plan) =>
        JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true });

    private static UploadedMediaSpec Upload(string data = "data:audio/wav;base64,QUJD") =>
        new(data, "clip.wav");

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

    private static StageSpec Stage(int index) => new(
        Id: index,
        Control: 1,
        Upscale: 1,
        UpscaleMethod: "pixel-lanczos",
        Model: "ltx-2",
        Steps: 8,
        CfgScale: 1,
        Sampler: "euler",
        Scheduler: "normal",
        ImageReference: "Generated",
        ClipStageIndex: index);
}
