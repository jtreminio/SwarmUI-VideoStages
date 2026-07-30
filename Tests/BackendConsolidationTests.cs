using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Covers the consolidated backend decisions: one diagnostic channel, the fail-closed paths, the
/// unified audio-source vocabulary, and the named stage-input dispatch.
/// </summary>
[Collection("VideoStagesTests")]
public class BackendConsolidationTests
{
    // --- 5a: one diagnostic type, with warnings persisted through the host output channel -----

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
        // A continue boundary the clip frame budget cannot fund degrades silently today; the plan
        // must still carry the warning so the reporter can hand it to the user.
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

    // --- 5b: fail closed where the docs promise it -------------------------------------------

    [Fact]
    public void Refine_source_install_fails_closed_against_a_plan_committed_to_refine()
    {
        RootPlan refinePlan = new(
            HostRootKind.GlobalRefineSource,
            RootUse.GlobalRefineReplacement,
            HostCoreDisposition.Drop,
            TimelineOutputDisposition.PublishTimelineOutput,
            NativeAudioDisposition.UseGlobalRefineAudio);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => RefineSourceInstallPolicy.RequiresInstall(refinePlan, hasVideoRefineSource: false));

        Assert.Contains("global refine", error.Message);
        Assert.True(RefineSourceInstallPolicy.RequiresInstall(refinePlan, hasVideoRefineSource: true));
    }

    [Fact]
    public void Refine_source_install_is_skipped_when_the_plan_never_committed_to_refine()
    {
        RootPlan normalPlan = new(
            HostRootKind.ImageToVideo,
            RootUse.ClipZeroSeed,
            HostCoreDisposition.Handoff,
            TimelineOutputDisposition.PublishTimelineOutput,
            NativeAudioDisposition.MakeAvailableToTimeline);

        Assert.False(
            RefineSourceInstallPolicy.RequiresInstall(normalPlan, hasVideoRefineSource: false));
        Assert.False(
            RefineSourceInstallPolicy.RequiresInstall(normalPlan, hasVideoRefineSource: true));
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

    // --- 5e: one audio-source vocabulary with one agreed unknown-input behaviour ---------------

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
    public void Unknown_audio_source_blocks_the_plan_exactly_once()
    {
        VideoExecutionPlan plan = TestPlanCompiler.Compile(new VideoStagesSpec(
            512,
            512,
            24,
            false,
            [ClipWithAudioSource(0, "not-a-real-source")]));

        PlanDiagnostic error = Assert.Single(
            PlanDiagnosticReporter.Errors(plan.Diagnostics)
                .Where(diagnostic => diagnostic.Message.Contains("not-a-real-source")));
        Assert.Equal(AudioBaseSourcePlanCompiler.UnknownSourceCode, error.Code);
        Assert.Equal(0, error.ClipId);
        // The capability validator no longer disagrees with a second, softer verdict.
        Assert.Equal(
            AudioSourceKind.Unknown,
            Assert.Single(plan.Clips).Audio.Base.Kind);
    }

    // --- 5e: one stable-node-id allocation map ------------------------------------------------

    [Fact]
    public void Stable_node_id_blocks_cannot_collide()
    {
        foreach (StableNodeIds.Block left in StableNodeIds.All)
        {
            foreach (StableNodeIds.Block right in StableNodeIds.All)
            {
                if (left.Name == right.Name)
                {
                    continue;
                }
                Assert.True(
                    left.EndExclusive <= right.Base || right.EndExclusive <= left.Base,
                    $"'{left.Name}' and '{right.Name}' overlap.");
            }
        }
    }

    [Fact]
    public void Stable_node_id_rejects_a_slot_outside_its_block()
    {
        WorkflowGenerator generator = new() { Workflow = new JObject(), UserInput = new(null) };

        Assert.Throws<InvalidOperationException>(() => StableNodeIds.Id(
            generator,
            StableNodeIds.AudioWindowMask,
            StableNodeIds.AudioWindowMask.Width));
    }

    // --- 5e: authored time parsing is structural ------------------------------------------------

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
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ClipTimelineSpecParser.CalculateStructuralFrameCount(
                double.PositiveInfinity,
                24));
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

    // --- 5e: published rule constraints are the values the evaluator enforces ------------------

    [Fact]
    public void Conditional_rule_thresholds_come_from_the_published_constraints()
    {
        Assert.Equal(
            Ltx2ConditionalRulePolicySource.AudioReuseRequiresThreeStages
                .Require<MinimumActiveStagesRuleConstraints>().MinimumActiveStages,
            Ltx2ConditionalRulePolicySource.AudioReuseMinimumActiveStages);
        Assert.Equal(
            Ltx2ConditionalRulePolicySource.HdrRequiresUniformTimeline
                .Require<UniformTimelineFeatureRuleConstraints>().MinimumTimelineClips,
            Ltx2ConditionalRulePolicySource.HdrMinimumTimelineClips);
        Assert.Equal(
            [ArchitectureEntryMode.SourceVideo, ArchitectureEntryMode.RefineVideo],
            Ltx2ConditionalRulePolicySource.RetakeEntryModes);
    }

    // --- 5f: one bounded dispatch names every stage-input case ---------------------------------

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
                SourcedFootageIsStageInput: true,
                RefinesIncomingLatent: true,
                PriorStageLatentIsReusable: true)));
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
    [InlineData(nameof(StageInputFacts.SourcedFootageIsStageInput), (int)StageInputCase.SourcedFootage)]
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
            nameof(StageInputFacts.SourcedFootageIsStageInput) =>
                Facts() with { SourcedFootageIsStageInput = true },
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
        StageInputCase actual = StageInputDispatcher.Resolve(Facts());

        Assert.Equal(StageInputCase.GuideReinjection, actual);
        Assert.False(StageInputDispatcher.SkipsGuideReinjection(actual));
    }

    [Fact]
    public void Every_stage_input_case_is_reachable_from_the_dispatch()
    {
        HashSet<StageInputCase> produced = [];
        for (int mask = 0; mask < 1 << 8; mask++)
        {
            produced.Add(StageInputDispatcher.Resolve(new StageInputFacts(
                HasPrimaryGuide: (mask & 1) != 0,
                PrimaryGuideIsStageInput: (mask & 2) != 0,
                IsContinuationTail: (mask & 4) != 0,
                HasOtherFrameReferences: (mask & 8) != 0,
                ReplacesTextToVideoRoot: (mask & 16) != 0,
                SourcedFootageIsStageInput: (mask & 32) != 0,
                RefinesIncomingLatent: (mask & 64) != 0,
                PriorStageLatentIsReusable: (mask & 128) != 0)));
        }

        Assert.Equal(Enum.GetValues<StageInputCase>().ToHashSet(), produced);
    }

    // --- 5g: the stage orchestrator is gone, StageRunner owns the layer ------------------------

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
        SourcedFootageIsStageInput: false,
        RefinesIncomingLatent: false,
        PriorStageLatentIsReusable: false);

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
