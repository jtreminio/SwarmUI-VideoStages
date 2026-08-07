using VideoStages.Authoring;
using VideoStages.Planning;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class PlanningCompilerComponentTests
{
    [Fact]
    public void BoundaryPlanCompiler_ReportsContinueFallbackAsImmutableResult()
    {
        TimelineSpec spec = new(640, 360, 24, false,
        [
            GeneratedClip(0, Stage(10)) with
            {
                BoundaryOut = Constants.BoundaryOutContinue,
                BoundaryOutOverlap = 16,
                BoundaryOutCarryAudio = true,
            },
            InitVideoClip(1, Stage(11)),
        ]);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        BoundaryPlanningResult result = BoundaryPlanCompiler.Compile(spec.Clips, plan.Clips);

        BoundaryPlan boundary = Assert.Single(result.Boundaries);
        Assert.Equal(BoundaryJoinType.Cut, boundary.EffectiveJoin);
        Assert.Equal(BoundaryFallbackReason.TargetHasInitVideo, boundary.Fallback);
        Assert.Equal(0, boundary.OverlapFrames);
        Assert.False(boundary.CarryAudio);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "boundary-targethasinitvideo");
    }

    [Fact]
    public void BoundaryPlanCompiler_CarriesAudioOnlyForEffectiveOverlappedBoundary()
    {
        TimelineSpec spec = new(640, 360, 24, false,
        [
            GeneratedClip(0, Stage(10)) with
            {
                BoundaryOut = Constants.BoundaryOutCrossfade,
                BoundaryOutOverlap = 16,
                BoundaryOutCarryAudio = true,
            },
            GeneratedClip(1, Stage(11)),
        ]);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        BoundaryPlan boundary = Assert.Single(
            BoundaryPlanCompiler.Compile(spec.Clips, plan.Clips).Boundaries);

        Assert.Equal(BoundaryJoinType.Crossfade, boundary.EffectiveJoin);
        Assert.True(boundary.CarryAudio);
    }

    [Fact]
    public void BoundaryPlanCompiler_RejectsMisalignedCompiledClips()
    {
        TimelineSpec spec = new(640, 360, 24, false,
        [
            GeneratedClip(0, Stage(10)),
            GeneratedClip(1, Stage(11)),
        ]);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);
        ClipPlan[] misaligned = [plan.Clips[0], plan.Clips[1] with { ClipId = 9 }];

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => BoundaryPlanCompiler.Compile(spec.Clips, misaligned));

        Assert.Contains("requires aligned authored and compiled clips", error.Message);
    }

    [Fact]
    public void BoundaryPlanCompiler_CutsContinueIntoRuntimeDerivedDuration()
    {
        TimelineSpec spec = new(640, 360, 24, false,
        [
            GeneratedClip(0, Stage(10)) with
            {
                BoundaryOut = Constants.BoundaryOutContinue,
            },
            GeneratedClip(1, Stage(11)) with
            {
                ClipLengthFromControlNet = true,
            },
        ]);

        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);
        BoundaryPlan boundary = Assert.Single(plan.Boundaries);

        Assert.Equal(BoundaryJoinType.Cut, boundary.EffectiveJoin);
        Assert.Equal(BoundaryFallbackReason.TargetHasDerivedDuration, boundary.Fallback);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "boundary-targethasderivedduration");
    }

    [Fact]
    public void BoundaryPlanCompiler_CutsContinueIntoAPassthroughOnlyTarget()
    {
        TimelineSpec spec = new(640, 360, 24, false,
        [
            GeneratedClip(0, Stage(10)) with
            {
                BoundaryOut = Constants.BoundaryOutContinue,
            },
            GeneratedClip(1, Stage(11) with { Control = 0 }),
        ]);

        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);
        BoundaryPlan boundary = Assert.Single(plan.Boundaries);

        Assert.Equal(BoundaryJoinType.Cut, boundary.EffectiveJoin);
        Assert.Equal(BoundaryFallbackReason.TargetHasNoStage, boundary.Fallback);
    }

    [Fact]
    public void BoundaryPlanCompiler_UsesArchitecturePolicyForRequirementsAndFrameGrid()
    {
        TimelineSpec spec = new(640, 360, 24, false,
        [
            GeneratedClip(0, Stage(10)) with
            {
                BoundaryOut = Constants.BoundaryOutContinue,
                BoundaryOutOverlap = 18,
                BoundaryOutCarryAudio = true,
            },
            InitVideoClip(1, Stage(11)) with
            {
                FrameRefs = [new FrameRefSpec("Upload", 1, false, "first.png")],
            },
        ]);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);
        ClipPlan[] planned =
        [
            plan.Clips[0] with
            {
                Architecture = plan.Clips[0].Architecture with
                {
                    BoundaryPolicy = FakeBoundaryPolicy,
                },
            },
            plan.Clips[1],
        ];

        BoundaryPlan boundary = Assert.Single(
            BoundaryPlanCompiler.Compile(spec.Clips, planned).Boundaries);

        Assert.Equal(BoundaryJoinType.Continue, boundary.EffectiveJoin);
        Assert.Equal(15, boundary.OverlapFrames);
        Assert.Equal(18, boundary.ContinuityWindowFrames);
        Assert.Equal(5, boundary.FrameStep);
    }

    [Fact]
    public void BoundaryPlanCompiler_SeparatesReferenceContextFromPixelOverlap()
    {
        TimelineSpec spec = new(640, 360, 24, false,
        [
            GeneratedClip(0, Stage(10)) with
            {
                BoundaryOut = Constants.BoundaryOutContinue,
                BoundaryOutOverlap = 18,
            },
            GeneratedClip(1, Stage(11)),
        ]);
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);
        BoundaryRuleConstraints constraints = FakeBoundaryPolicy
            .Rules[BoundaryJoinType.Continue]
            .Constraints with
        {
            ContinueMode = ContinueBoundaryMode.Reference,
        };
        ArchitectureBoundaryPolicy policy = new(new Dictionary<BoundaryJoinType, RuleDecision>
        {
            [BoundaryJoinType.Continue] = RuleDecision.Conditional(
                "fake.reference-continue",
                "Reference context.",
                constraints),
        });
        ClipPlan[] planned =
        [
            plan.Clips[0] with
            {
                Architecture = plan.Clips[0].Architecture with { BoundaryPolicy = policy },
            },
            plan.Clips[1],
        ];

        BoundaryPlan boundary = Assert.Single(
            BoundaryPlanCompiler.Compile(spec.Clips, planned).Boundaries);

        Assert.Equal(BoundaryJoinType.Continue, boundary.EffectiveJoin);
        Assert.Equal(ContinueBoundaryMode.Reference, boundary.ContinueMode);
        Assert.Equal(0, boundary.OverlapFrames);
        Assert.Equal(15, boundary.ContinuityWindowFrames);
        Assert.False(boundary.CarryAudio);
    }

    [Fact]
    public void FocusedReferencePromptAndLoraCompilers_PlanStageAdornment()
    {
        StageSpec stage = Stage(11, rawIndex: 1) with
        {
            ImageReference = "edit4",
            FrameRefStrengths = [0.25],
            ControlNetStrength = 0.6,
            Loras = [new LoraRef("stage", 0.5)],
        };
        ClipSpec clip = GeneratedClip(0, Stage(10), stage) with
        {
            Loras = [new LoraRef("clip", 0.75)],
            PromptWindows = [new PromptWindowSpec("prompt", 0, 1)],
            FrameRefs = [new FrameRefSpec("Upload", 1, false, "ref.png", "data:image/png;base64,QQ==")],
            IcLoras =
            [
                new IcLoraSpec(
                    "adapter.safetensors",
                    MediaSource.ControlNetTwo,
                    1,
                    1,
                    Constants.IcLoraControlCanny,
                    null,
                    DriveData: IcLoraDriveData.Visual,
                    Stage: 1),
            ],
        };

        PromptRelayPlan prompt = PromptRelayPlanCompiler.Compile(clip, 24);
        ArchitectureClipCompilation compilation = Ltx2ClipPlanCompiler.Compile(
            clip,
            new(640, 360, 24, ArchitectureEntryMode.ImageToVideo));
        GuideReferencePlan guide = Assert.IsType<Ltx2StagePayload>(
            compilation.StagePayloads[stage.ClipStageRawIndex]).Guide;
        var loras = LoraPlanCompiler.Compile(clip, stage, LoraTarget.ModelAndTextEncoder);
        var icLoras = IcLoraPlanCompiler
            .CompileClip(clip, new(640, 360, 24, ArchitectureEntryMode.ImageToVideo))
            .Stages[stage.ClipStageRawIndex];
        var references = FrameRefPlanCompiler.Compile(clip, stage);

        Assert.Equal(PromptRelayMode.Relay, prompt.Mode);
        Assert.Equal(
            new GuideReferencePlan(StageGuideReferenceKind.Base2Edit, "edit4", 4),
            guide);
        Assert.Collection(
            loras,
            lora => Assert.Equal("clip", lora.Name),
            lora => Assert.Equal("stage", lora.Name));
        Assert.Equal(IcLoraMediaSourceKind.ControlNet, Assert.Single(icLoras).Drive.Source);
        Assert.Equal(1, icLoras[0].Drive.ControlNetIndex);
        Assert.Equal(FrameRefSourceKind.Upload, Assert.Single(references).SourceKind);
    }

    [Fact]
    public void LoraPlanCompiler_UsesStageWeightsAlignedToClipDefinitions()
    {
        StageSpec stage = Stage(10) with
        {
            LoraWeights = [0.35, -0.2],
        };
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            Loras =
            [
                new LoraRef("cinematic", 1),
                new LoraRef("detail", 0.8),
            ],
        };

        var plans = LoraPlanCompiler.Compile(clip, stage, LoraTarget.ModelAndTextEncoder);

        Assert.Collection(
            plans,
            lora =>
            {
                Assert.Equal("cinematic", lora.Name);
                Assert.Equal(0.35, lora.ModelWeight);
                Assert.Equal(0.35, lora.TextEncoderWeight);
            },
            lora =>
            {
                Assert.Equal("detail", lora.Name);
                Assert.Equal(-0.2, lora.ModelWeight);
                Assert.Equal(-0.2, lora.TextEncoderWeight);
            });
    }

    [Fact]
    public void LoraPlanCompiler_SkipsZeroWeightClipDefinitions()
    {
        StageSpec stage = Stage(10) with { LoraWeights = [0] };
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            Loras = [new LoraRef("disabled-for-this-stage", 1)],
        };

        Assert.Empty(LoraPlanCompiler.Compile(clip, stage, LoraTarget.ModelAndTextEncoder));
    }

    [Fact]
    public void LoraPlanCompiler_OmitsNoOpDirectDefinitionsAndRetainsTextOnlyDefinitions()
    {
        StageSpec stage = Stage(10) with
        {
            Loras =
            [
                new("stage-no-op", 0, 0),
                new("stage-text-only", 0, 0.8),
            ],
        };
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            Loras =
            [
                new("clip-no-op", 0, 0),
                new("clip-text-only", 0, 0.6),
            ],
        };

        Assert.Collection(
            LoraPlanCompiler.Compile(clip, stage, LoraTarget.ModelAndTextEncoder),
            plan =>
            {
                Assert.Equal("clip-text-only", plan.Name);
                Assert.Equal(0, plan.ModelWeight);
                Assert.Equal(0.6, plan.TextEncoderWeight);
            },
            plan =>
            {
                Assert.Equal("stage-text-only", plan.Name);
                Assert.Equal(0, plan.ModelWeight);
                Assert.Equal(0.8, plan.TextEncoderWeight);
            });
    }

    [Fact]
    public void LoraPlanCompiler_ModelOnlyPolicy_OmitsTextOnlyDefinitions()
    {
        StageSpec stage = Stage(10) with
        {
            Loras =
            [
                new("stage-text-only", 0, 0.8),
                new("stage-model", 0.4, 0.9),
            ],
        };
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            Loras =
            [
                new("clip-text-only", 0, 0.6),
                new("clip-model", -0.3, 0.7),
            ],
        };

        Assert.Collection(
            LoraPlanCompiler.Compile(
                clip,
                stage,
                LoraTarget.ModelOnly),
            plan =>
            {
                Assert.Equal("clip-model", plan.Name);
                Assert.Equal(-0.3, plan.ModelWeight);
                Assert.Equal(0.7, plan.TextEncoderWeight);
            },
            plan =>
            {
                Assert.Equal("stage-model", plan.Name);
                Assert.Equal(0.4, plan.ModelWeight);
                Assert.Equal(0.9, plan.TextEncoderWeight);
            });
    }

    [Fact]
    public void IcLoraPlanCompiler_UsesPerEntryStageStrengths()
    {
        StageSpec stage = Stage(11) with
        {
            ControlNetStrength = 0.6,
            IcLoraStrengths = [0.2, 0.9],
        };
        ClipSpec clip = GeneratedClip(0, stage) with
        {
            IcLoras =
            [
                new IcLoraSpec(
                    "adapter-one.safetensors",
                    MediaSource.ControlNetOne,
                    1,
                    1,
                    Constants.IcLoraControlCanny,
                    null,
                    DriveData: IcLoraDriveData.Visual),
                new IcLoraSpec(
                    "adapter-two.safetensors",
                    MediaSource.ControlNetTwo,
                    1,
                    1,
                    Constants.IcLoraControlDepth,
                    null,
                    DriveData: IcLoraDriveData.Visual),
            ],
        };

        var plans = IcLoraPlanCompiler
            .CompileClip(clip, new(640, 360, 24, ArchitectureEntryMode.ImageToVideo))
            .Stages[stage.ClipStageRawIndex];

        Assert.Equal(2, plans.Length);
        Assert.Equal(0.2, plans[0].GuideStrength);
        Assert.Equal(0.9, plans[1].GuideStrength);
    }

    [Theory]
    [InlineData(2, "Upload", "none", null, (int)IcLoraDriveData.None, "ltx2.ic-lora.stage-target-invalid")]
    [InlineData(-1, "future-source", "none", null, (int)IcLoraDriveData.Visual, "ltx2.ic-lora.drive-source-unsupported")]
    [InlineData(-1, "Upload", "future-control", null, (int)IcLoraDriveData.None, "ltx2.ic-lora.control-mode-unsupported")]
    [InlineData(
        -1,
        "Upload",
        "none",
        "data:application/octet-stream;base64,QQ==",
        (int)IcLoraDriveData.Visual,
        "ltx2.ic-lora.drive-media-kind-unsupported")]
    public void LtxIcLoraStructuralWarnings_DropInvalidEntries(
        int targetStage,
        string source,
        string control,
        string uploadedData,
        int driveData,
        string expectedCode)
    {
        ClipSpec clip = GeneratedClip(0, Stage(10)) with
        {
            AuthoredStages = [new(0, "ltx-2", "ltx-2.3", false)],
            IcLoras =
            [
                new(
                    "adapter.safetensors",
                    source,
                    1,
                    1,
                    control,
                    uploadedData is null ? null : new(uploadedData, "drive.bin"),
                    DriveData: (IcLoraDriveData)driveData,
                    Stage: targetStage),
            ],
        };

        ArchitectureClipCompilation compilation = Ltx2ClipPlanCompiler.Compile(
            clip,
            new(640, 360, 24, ArchitectureEntryMode.ImageToVideo));

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Code == expectedCode
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
        Assert.Empty(Assert.IsType<Ltx2StagePayload>(
            compilation.StagePayloads[clip.Stages[0].ClipStageRawIndex]).IcLoras);
    }

    [Fact]
    public void LtxIcLoraStructuralWarning_PublishesPayloadWithoutInvalidEntry()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10)) with
        {
            AuthoredStages = [new(0, "ltx-2", "ltx-2.3", false)],
            IcLoras =
            [
                new(
                    "adapter.safetensors",
                    "future-source",
                    1,
                    1,
                    Constants.IcLoraControlNone,
                    null,
                    DriveData: IcLoraDriveData.Visual),
            ],
        };

        VideoExecutionPlan plan = TestPlanCompiler.Compile(new(
            640,
            360,
            24,
            false,
            [clip]));
        ClipPlan plannedClip = Assert.Single(plan.Clips);

        Assert.NotNull(plannedClip.ArchitecturePayload);
        Assert.Empty(Assert.Single(plannedClip.Stages).RequireLtx2Payload().IcLoras);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "ltx2.ic-lora.drive-source-unsupported"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error);
    }

    [Fact]
    public void ClipPlanCompiler_PlansInitVideoStageChainAndOutputOwnership()
    {
        ClipSpec clip = InitVideoClip(7, Stage(10, control: 0), Stage(11, rawIndex: 1));
        ArchitectureClipCompilation compilation = Ltx2ClipPlanCompiler.Compile(
            clip,
            new(640, 360, 30, ArchitectureEntryMode.InitVideo));

        ClipPlan plan = ClipPlanCompiler.Compile(
            clip,
            new ClipPlanCompilationContext(
                Width: 640,
                Height: 360,
                FramesPerSecond: 30,
                TotalStageCount: 2,
                FirstStageOrdinal: 0,
                EntryMode: ArchitectureEntryMode.InitVideo,
                ArchitectureCompilation: compilation));

        Assert.Equal(ArchitectureEntryMode.InitVideo, plan.EntryMode);
        Assert.True(plan.Stages[0].IsPassthrough);
        Assert.Equal(StageInputKind.PreviousStage, plan.Stages[1].Input);
        Assert.False(plan.Stages[1].IsPassthrough);
        Assert.Equal(640, plan.InitVideo.TargetWidth);
    }

    [Theory]
    [InlineData((int)ArchitectureEntryMode.InitVideo, false)]
    [InlineData((int)ArchitectureEntryMode.ImageToVideo, true)]
    public void ClipPlanCompiler_rejects_mismatched_init_video_entry(
        int entryMode,
        bool hasInitVideo)
    {
        ClipSpec clip = InitVideoClip(7) with
        {
            InitVideo = hasInitVideo
                ? new("data", "source.mp4", 0)
                : null,
        };
        ClipPlanCompilationContext context = new(
            Width: 640,
            Height: 360,
            FramesPerSecond: 30,
            TotalStageCount: 0,
            FirstStageOrdinal: 0,
            EntryMode: (ArchitectureEntryMode)entryMode);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ClipPlanCompiler.Compile(clip, context));

        Assert.Contains("entry mode and init-video plan disagree", error.Message);
    }

    [Fact]
    public void ClipPlanCompiler_FailsClosedWhenRawStagePayloadIsMissing()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, rawIndex: 3));
        TestClipPayload clipPayload = new(new("test"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ClipPlanCompiler.Compile(
                clip,
                Context(
                    clipPayload,
                    new Dictionary<int, IArchitectureStagePayload>())));

        Assert.Equal(
            "VideoStages bug: Clip stage 3 has no architecture stage payload.",
            error.Message);
    }

    [Fact]
    public void ClipPlanCompiler_AcceptsSparseAuthoredRawStagePayloadKeys()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, rawIndex: 3));
        TestClipPayload clipPayload = new(new("test"));
        TestStagePayload stagePayload = new(clipPayload.ArchitectureId);

        ClipPlan plan = ClipPlanCompiler.Compile(
            clip,
            Context(
                clipPayload,
                new Dictionary<int, IArchitectureStagePayload>
                {
                    [3] = stagePayload,
                }));

        Assert.Equal(3, Assert.Single(plan.Stages).ClipStageRawIndex);
        Assert.Same(stagePayload, Assert.Single(plan.Stages).ArchitecturePayload);
    }

    [Fact]
    public void ClipPlanCompiler_FailsClosedWhenClipPayloadDiffersFromAssignedArchitecture()
    {
        ClipSpec clip = GeneratedClip(0, Stage(10, rawIndex: 3));
        ArchitectureId payloadArchitectureId = new("wrong-architecture");
        ClipArchitectureAssignment assignment = new(
            clip.Id,
            Ltx2ArchitectureModule.Instance,
            Ltx2ArchitectureModule.Instance.Descriptor,
            new Dictionary<int, ResolvedVideoModel>());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ClipPlanCompiler.Compile(
                clip,
                Context(
                    new TestClipPayload(payloadArchitectureId),
                    new Dictionary<int, IArchitectureStagePayload>
                    {
                        [3] = new TestStagePayload(payloadArchitectureId),
                    },
                    assignment)));

        Assert.Equal(
            "VideoStages bug: Clip payload architecture 'wrong-architecture' does not match assigned architecture "
                + "'ltx2'.",
            error.Message);
    }

    [Fact]
    public void SourceOnlyDescriptorCompilesWithoutAModule()
    {
        ClipSpec clip = InitVideoClip(7);
        VideoArchitectureRegistration registration = Assert.Single(
            VideoArchitectureManifest.Production,
            item => item.Descriptor.Id == NoneArchitecture.Id);

        Assert.Null(registration.Module);
        ClipPlan compiled = Assert.Single(TestPlanCompiler.Compile(
            new TimelineSpec(640, 360, 30, false, [clip])).Clips);
        Assert.IsType<NoneClipPayload>(compiled.ArchitecturePayload);
        Assert.Empty(compiled.Stages);
    }

    [Fact]
    public void ClipGeometryValidator_WarnsBeforeGenerationWhenAClipWillBeConformed()
    {
        TimelineSpec spec = new(512, 512, 24, false,
        [
            GeneratedClip(0, Stage(10) with { Upscale = 2 }),
            GeneratedClip(1, Stage(11)),
        ]);

        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        PlanDiagnostic diagnostic = Assert.Single(
            plan.Diagnostics,
            entry => entry.Code == "clip-geometry-will-conform");
        Assert.Equal(PlanDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(0, diagnostic.ClipId);
        Assert.Contains("1024x1024", diagnostic.Message);
        Assert.Contains("512x512", diagnostic.Message);
    }

    [Fact]
    public void ClipGeometryValidator_StaysSilentWhenEveryClipEndsAtTheSameSize()
    {
        TimelineSpec spec = new(512, 512, 24, false,
        [
            GeneratedClip(0, Stage(10) with { Upscale = 2 }),
            GeneratedClip(1, Stage(11) with { Upscale = 2 }),
        ]);

        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        Assert.DoesNotContain(
            plan.Diagnostics,
            entry => entry.Code is "clip-geometry-will-conform" or "clip-aspect-mismatch");
    }

    [Fact]
    public void ClipGeometryValidator_ProjectsStagelessInitVideoClipsInsteadOfGoingSilent()
    {
        TimelineSpec spec = new(512, 512, 24, false,
        [
            InitVideoClip(0),
            GeneratedClip(1, Stage(11) with { Upscale = 2 }),
        ]);

        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);

        PlanDiagnostic diagnostic = Assert.Single(
            plan.Diagnostics,
            entry => entry.Code == "clip-geometry-will-conform");
        Assert.Equal(1, diagnostic.ClipId);
        Assert.Contains("1024x1024", diagnostic.Message);
        Assert.Contains("512x512", diagnostic.Message);
    }

    private static ClipSpec GeneratedClip(int id, params StageSpec[] stages) =>
        SpecFixtures.Clip(id, stages);

    private static ClipSpec InitVideoClip(int id, params StageSpec[] stages) =>
        new(id, 49, MediaSource.Native, [], false, false, false, false, null, [], stages,
            InitVideo: new InitVideoSpec("data:video/mp4;base64,QQ==", "source.mp4", 1.5));

    private static StageSpec Stage(int id, double control = 1, int? rawIndex = null) =>
        SpecFixtures.Stage(
            id,
            control: control,
            clipStageIndex: id - 10,
            clipStageRawIndex: rawIndex ?? id - 10);

    private static ClipPlanCompilationContext Context(
        IArchitectureClipPayload clipPayload,
        IReadOnlyDictionary<int, IArchitectureStagePayload> stagePayloads,
        ClipArchitectureAssignment architecture = null) =>
        new(
            Width: 512,
            Height: 512,
            FramesPerSecond: 24,
            TotalStageCount: 1,
            FirstStageOrdinal: 0,
            EntryMode: ArchitectureEntryMode.ImageToVideo,
            Assignment: architecture,
            ArchitectureCompilation: new(clipPayload, stagePayloads, []));

    private sealed record TestClipPayload(
        ArchitectureId ArchitectureId) : IArchitectureClipPayload;

    private sealed record TestStagePayload(
        ArchitectureId ArchitectureId) : IArchitectureStagePayload
    {
        public StageCorePlan Core => TestPlanCompiler.DefaultStageCore;
    }

    private static readonly ArchitectureBoundaryPolicy FakeBoundaryPolicy =
        new ArchitectureBoundaryPolicy(
            new Dictionary<BoundaryJoinType, RuleDecision>
            {
                [BoundaryJoinType.Continue] = RuleDecision.Conditional(
                    "fake.continue",
                    "Fake policy with a different grid and permissive target.",
                    new BoundaryRuleConstraints(
                        FrameStep: 5,
                        MinFrames: 10,
                        MaxFrames: 30,
                        DefaultFrames: 15,
                        ContinuityExtraFrames: 3,
                        TargetRequiresGeneratedEntry: false,
                        TargetRequiresStage: false,
                        TargetDisallowsInitialReference: false)),
            });
}
