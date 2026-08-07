using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Execution;
using VideoStages.Execution.StockHost;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class BackendConsolidationTests
{
    [Fact]
    public void Reporter_routes_each_severity_to_its_supplied_sink()
    {
        List<string> warnings = [];
        List<string> infos = [];
        List<string> errors = [];

        PlanDiagnosticReporter.Report(
            [
                new(PlanDiagnosticSeverity.Warning, "w", "a boundary degraded", ClipId: 3),
                new(PlanDiagnosticSeverity.Info, "i", "tracks overlap"),
                new(PlanDiagnosticSeverity.Error, "e", "no architecture"),
            ],
            warnings.Add,
            infos.Add,
            errors.Add);

        Assert.Equal(["VideoStages: a boundary degraded (clip 3)"], warnings);
        Assert.Equal(["VideoStages: tracks overlap"], infos);
        Assert.Equal(["VideoStages: no architecture"], errors);
    }

    [Fact]
    public void Request_reporter_persists_deduplicated_warnings_in_host_output_metadata()
    {
        T2IParamInput input = new(null);
        PlanDiagnostic warning =
            new(PlanDiagnosticSeverity.Warning, "w", "a boundary degraded", ClipId: 3);

        PlanDiagnosticReporter.ReportToRequest(
            [
                warning,
                warning,
                new(PlanDiagnosticSeverity.Info, "i", "tracks overlap"),
                new(PlanDiagnosticSeverity.Error, "e", "no architecture"),
            ],
            input);
        PlanDiagnosticReporter.ReportToRequest([warning], input);

        List<string> warnings = Assert.IsType<List<string>>(
            input.ExtraMeta["parser_warnings"]);
        Assert.Equal(
            ["VideoStages: a boundary degraded (clip 3)"],
            warnings);
        Assert.Equal(
            "VideoStages: a boundary degraded (clip 3)",
            input.BuildExtraDataJObject()["parser_warnings"]?[0]?.Value<string>());
    }

    [Fact]
    public void Request_reporter_detaches_a_cloned_inputs_shared_prompt_warning_list()
    {
        T2IParamInput original = new(null);
        original.ExtraMeta["parser_warnings"] = new List<string> { "Prompt warning." };
        T2IParamInput clone = original.Clone();

        PlanDiagnosticReporter.ReportToRequest(
            [new(PlanDiagnosticSeverity.Warning, "w", "an option was ignored")],
            clone);

        Assert.Equal(
            ["Prompt warning."],
            Assert.IsType<List<string>>(original.ExtraMeta["parser_warnings"]));
        Assert.Equal(
            ["Prompt warning.", "VideoStages: an option was ignored"],
            Assert.IsType<List<string>>(clone.ExtraMeta["parser_warnings"]));
    }

    [Fact]
    public void Reporter_reports_a_repeated_rule_once_and_names_every_identity_it_knows()
    {
        List<string> warnings = [];

        PlanDiagnosticReporter.Report(
            [
                new(PlanDiagnosticSeverity.Warning, "w", "same", ClipId: 1),
                new(PlanDiagnosticSeverity.Warning, "w", "same", ClipId: 1),
                new(
                    PlanDiagnosticSeverity.Warning,
                    "w2",
                    "span pending",
                    ClipId: 2,
                    StageId: 5,
                    TrackId: "bed"),
            ],
            warnings.Add,
            _ => { },
            _ => { });

        Assert.Equal(
            [
                "VideoStages: same (clip 1)",
                "VideoStages: span pending (clip 2, stage 5, audio track 'bed')",
            ],
            warnings);
    }

    [Fact]
    public void Plan_warnings_survive_compilation_instead_of_being_computed_and_discarded()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(new TimelineSpec(
            512,
            512,
            24,
            false,
            [
                ClipWithBoundary(0, frames: 9, Constants.BoundaryOutContinue),
                ClipWithBoundary(1, frames: 9, null),
            ]));

        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Warning);
        Assert.Empty(PlanDiagnosticReporter.Errors(plan.Diagnostics));
        List<string> warnings = [];
        PlanDiagnosticReporter.Report(plan.Diagnostics, warnings.Add, _ => { }, _ => { });
        Assert.NotEmpty(warnings);
        Assert.All(warnings, line => Assert.StartsWith("VideoStages: ", line));
    }

    [Fact]
    public void Global_frame_trim_fails_closed_instead_of_dropping_a_requested_trim()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 2);
        WorkflowGenerator generator = new()
        {
            Workflow = new JObject(),
            UserInput = input,
        };

        RuntimeArtifact artifact;
        using (WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow))
        {
            artifact = RuntimeArtifact.Capture(generator, bridge);
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new Timeline.GlobalVideoFrameTrimmer(generator).Apply(artifact));

        Assert.Contains("global frame trim", error.Message);
    }

    [Fact]
    public void Global_frame_trim_still_does_nothing_when_no_trim_was_requested()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator generator = new()
        {
            Workflow = new JObject(),
            UserInput = new(null),
        };

        RuntimeArtifact artifact = new(null, null);

        Assert.Same(artifact, new Timeline.GlobalVideoFrameTrimmer(generator).Apply(artifact));
        Assert.Null(generator.CurrentMedia);
    }

    [Fact]
    public void Metadata_sanitizer_never_publishes_a_document_it_could_not_walk()
    {
        Assert.Equal(
            MetadataSanitizer.Unsanitizable,
            MetadataSanitizer.StripUploadDataFromJsonParameter("{\"data\":\"AAAA"));
    }

    [Theory]
    [InlineData("Native", (int)AudioSourceKind.Native)]
    [InlineData("", (int)AudioSourceKind.Native)]
    [InlineData(null, (int)AudioSourceKind.Native)]
    [InlineData("Upload", (int)AudioSourceKind.Upload)]
    [InlineData("ControlNet", (int)AudioSourceKind.ControlNet)]
    [InlineData("nonsense", (int)AudioSourceKind.Unknown)]
    public void One_parser_owns_the_audio_source_vocabulary(string raw, int expected)
    {
        Assert.Equal((AudioSourceKind)expected, AudioSource.Parse(raw).Kind);
    }

    [Fact]
    public void Unknown_audio_source_warns_once_and_falls_back_to_disabled()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(new TimelineSpec(
            512,
            512,
            24,
            false,
            [ClipWithAudioSource(0, "not-a-real-source")]));

        PlanDiagnostic warning = Assert.Single(
            plan.Diagnostics,
            diagnostic => diagnostic.Message.Contains("not-a-real-source"));
        Assert.Equal(PlanDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("audio.source.unknown", warning.Code);
        Assert.Equal(0, warning.ClipId);
        Assert.Equal(
            AudioSourceKind.Disabled,
            Assert.Single(plan.Clips).Audio.Base.Kind);
    }

    [Fact]
    public void Authored_duration_parsing_does_not_import_a_global_architecture_grid()
    {
        Assert.Equal(
            27,
            AuthoringTimeline.CalculateStructuralFrameCount(1.05, 24));
    }

    [Fact]
    public void Authored_duration_parsing_rejects_unrepresentable_counts()
    {
        Assert.Throws<OverflowException>(
            () => AuthoringTimeline.CalculateStructuralFrameCount(
                int.MaxValue,
                1));
    }

    [Fact]
    public void Retake_window_clamping_cannot_wrap_its_endpoint()
    {
        Assert.Equal(
            (int.MaxValue - 2, int.MaxValue),
            RetakeWindowSpec.ClampFrameWindow(
                int.MaxValue - 2,
                int.MaxValue,
                int.MaxValue));
    }

    private static ClipSpec ClipWithBoundary(int id, int frames, string boundary) => new(
        Id: id,
        Frames: frames,
        AudioSource: MediaSource.Native,
        IcLoras: [],
        SaveAudioTrack: false,
        ClipLengthFromAudio: false,
        ClipLengthFromControlNet: false,
        ReuseAudio: false,
        UploadedAudio: null,
        FrameRefs: [],
        Stages: [Stage(id)],
        BoundaryOut: boundary);

    private static ClipSpec ClipWithAudioSource(int id, string audioSource) => new(
        Id: id,
        Frames: 25,
        AudioSource: audioSource,
        IcLoras: [],
        SaveAudioTrack: false,
        ClipLengthFromAudio: false,
        ClipLengthFromControlNet: false,
        ReuseAudio: false,
        UploadedAudio: null,
        FrameRefs: [],
        Stages: [Stage(id)]);

    private static StageSpec Stage(int id) => new(
        Id: id,
        Control: 1,
        Upscale: 1,
        UpscaleMethod: "",
        Model: "unit-test-model",
        Steps: 8,
        CfgScale: 1,
        Sampler: "euler",
        Scheduler: "normal",
        ImageReference: "Generated");
}
