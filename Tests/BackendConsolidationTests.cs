using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using VideoStages.HostVideo;
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
                    TrackId: "bed",
                    SpanIndex: 0),
            ],
            warnings.Add,
            _ => { },
            _ => { });

        Assert.Equal(
            [
                "VideoStages: same (clip 1)",
                "VideoStages: span pending (clip 2, stage 5, audio track 'bed', span 0)",
            ],
            warnings);
    }

    [Fact]
    public void Plan_warnings_survive_compilation_instead_of_being_computed_and_discarded()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(new VideoStagesSpec(
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

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => new GlobalVideoFrameTrimmer(generator).Apply());

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

        new GlobalVideoFrameTrimmer(generator).Apply();

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
        Assert.Equal((AudioSourceKind)expected, AudioSourceParser.Parse(raw).Kind);
    }

    [Fact]
    public void Unknown_audio_source_warns_once_and_falls_back_to_disabled()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(new VideoStagesSpec(
            512,
            512,
            24,
            false,
            [ClipWithAudioSource(0, "not-a-real-source")]));

        PlanDiagnostic warning = Assert.Single(
            plan.Diagnostics,
            diagnostic => diagnostic.Message.Contains("not-a-real-source"));
        Assert.Equal(PlanDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(AudioBaseSourcePlanCompiler.UnknownSourceCode, warning.Code);
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
            ClipTimelineSpecParser.CalculateStructuralFrameCount(1.05, 24));
    }

    [Fact]
    public void Authored_duration_parsing_rejects_unrepresentable_counts()
    {
        Assert.Throws<OverflowException>(
            () => ClipTimelineSpecParser.CalculateStructuralFrameCount(
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

    [Fact]
    public void Stage_input_dispatch_names_the_primary_guide_cases_in_priority_order()
    {
        Assert.Equal(
            StageInputCase.PrimaryGuideIsStageInput,
            StageInputDispatcher.Resolve(new StageInputFacts(
                HasPrimaryGuide: true,
                PrimaryGuideIsStageInput: true,
                IsContinuationTail: true,
                HasOtherFrameReferences: true,
                ReplacesTextToVideoRoot: true,
                InitVideoFootageIsStageInput: true,
                RefinesIncomingLatent: true,
                PriorStageLatentIsReusable: true,
                HasGuide: true)));
        Assert.Equal(
            StageInputCase.ContinuationTail,
            StageInputDispatcher.Resolve(Facts() with
            {
                HasPrimaryGuide = true,
                IsContinuationTail = true,
            }));
        Assert.Equal(
            StageInputCase.AuthoredGuideReference,
            StageInputDispatcher.Resolve(Facts() with { HasPrimaryGuide = true }));
    }

    [Theory]
    [InlineData(nameof(StageInputFacts.ReplacesTextToVideoRoot), (int)StageInputCase.TextToVideoRootReplacement)]
    [InlineData(nameof(StageInputFacts.HasOtherFrameReferences), (int)StageInputCase.FrameReferencesOnly)]
    [InlineData(nameof(StageInputFacts.InitVideoFootageIsStageInput), (int)StageInputCase.InitVideoFootage)]
    [InlineData(nameof(StageInputFacts.RefinesIncomingLatent), (int)StageInputCase.IncomingLatentRefine)]
    [InlineData(nameof(StageInputFacts.PriorStageLatentIsReusable), (int)StageInputCase.PriorStageLatentReuse)]
    public void Stage_input_dispatch_names_every_guide_free_case(
        string fact,
        int expected)
    {
        StageInputFacts facts = fact switch
        {
            nameof(StageInputFacts.ReplacesTextToVideoRoot) =>
                Facts() with { ReplacesTextToVideoRoot = true },
            nameof(StageInputFacts.HasOtherFrameReferences) =>
                Facts() with { HasOtherFrameReferences = true },
            nameof(StageInputFacts.InitVideoFootageIsStageInput) =>
                Facts() with { InitVideoFootageIsStageInput = true },
            nameof(StageInputFacts.RefinesIncomingLatent) =>
                Facts() with { RefinesIncomingLatent = true },
            _ => Facts() with { PriorStageLatentIsReusable = true },
        };

        StageInputCase actual = StageInputDispatcher.Resolve(facts);

        Assert.Equal((StageInputCase)expected, actual);
        Assert.True(StageInputDispatcher.SkipsGuideReinjection(actual));
    }

    [Fact]
    public void Stage_input_dispatch_falls_back_to_reinjecting_the_resolved_guide()
    {
        StageInputCase actual = StageInputDispatcher.Resolve(Facts() with { HasGuide = true });

        Assert.Equal(StageInputCase.GuideReinjection, actual);
        Assert.False(StageInputDispatcher.SkipsGuideReinjection(actual));
    }

    [Fact]
    public void Stage_input_dispatch_skips_reinjection_when_no_guide_resolved()
    {
        StageInputCase actual = StageInputDispatcher.Resolve(Facts());

        Assert.Equal(StageInputCase.NoGuide, actual);
        Assert.True(StageInputDispatcher.SkipsGuideReinjection(actual));
    }

    [Fact]
    public void Every_stage_input_case_is_reachable_from_the_dispatch()
    {
        HashSet<StageInputCase> produced = [];
        for (int mask = 0; mask < 1 << 9; mask++)
        {
            produced.Add(StageInputDispatcher.Resolve(new StageInputFacts(
                HasPrimaryGuide: (mask & 1) != 0,
                PrimaryGuideIsStageInput: (mask & 2) != 0,
                IsContinuationTail: (mask & 4) != 0,
                HasOtherFrameReferences: (mask & 8) != 0,
                ReplacesTextToVideoRoot: (mask & 16) != 0,
                InitVideoFootageIsStageInput: (mask & 32) != 0,
                RefinesIncomingLatent: (mask & 64) != 0,
                PriorStageLatentIsReusable: (mask & 128) != 0,
                HasGuide: (mask & 256) != 0)));
        }

        Assert.Equal(Enum.GetValues<StageInputCase>().ToHashSet(), produced);
    }

    [Fact]
    public void Stage_execution_has_one_owner_per_layer()
    {
        Assert.Null(typeof(StageRunner).Assembly.GetType(
            "VideoStages.Architectures.Ltx2.LtxStageOrchestrator"));
        Assert.Equal(
            [
                typeof(WorkflowGenerator),
                typeof(LtxStageExecutor),
                typeof(LtxStageGuideMediaResolver),
                typeof(LtxClipRefResolver),
            ],
            Assert.Single(typeof(StageRunner).GetConstructors())
                .GetParameters()
                .Select(parameter => parameter.ParameterType));
    }

    private static StageInputFacts Facts() => new(
        HasPrimaryGuide: false,
        PrimaryGuideIsStageInput: false,
        IsContinuationTail: false,
        HasOtherFrameReferences: false,
        ReplacesTextToVideoRoot: false,
        InitVideoFootageIsStageInput: false,
        RefinesIncomingLatent: false,
        PriorStageLatentIsReusable: false,
        HasGuide: false);

    private static ClipSpec ClipWithBoundary(int id, int frames, string boundary) => new(
        Id: id,
        Frames: frames,
        AudioSource: Constants.AudioSourceNative,
        IcLoras: [],
        SaveAudioTrack: false,
        ClipLengthFromAudio: false,
        ClipLengthFromControlNet: false,
        ReuseAudio: false,
        UploadedAudio: null,
        ImageRefs: [],
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
        ImageRefs: [],
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
